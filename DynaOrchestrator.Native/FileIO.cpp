//
// Created by tianjia on 2026/3/17.
// Optimized for Massive I/O & High Performance Parsing (Windows Only)
//

#include "FileIO.h"
#include "Logger.h"

#include <string>
#include <fstream>
#include <iostream>
#include <algorithm>
#include <cmath>
#include <limits>
#include <cctype>
#include <stdexcept>
#include <cstring>
#include <omp.h>
#include <charconv>
#include <system_error>

// 专用于 Windows 平台的内存映射 API
#define NOMINMAX // <-- 新增这一行，禁用 Windows 默认的 min/max 宏
#include <windows.h>

namespace
{
	// ===================================================================
	// 基础几何数据结构定义
	// ===================================================================

	// 房间/场景的轴向包围盒，用于存储由 STL 提取出的六面主墙位置
	struct RoomBox
	{
		float min_x, max_x;
		float min_y, max_y;
		float min_z, max_z;
	};

	// 三维空间中的线段，由两个端点构成，用于表示墙壁的边缘
	struct Segment3
	{
		Point a;
		Point b;
	};

	// 具有相同法线轴向的三角面片组，用于平面聚类（提取主墙）
	struct AxisPlaneGroup
	{
		char axis;
		float plane_coord;
		std::vector<Triangle> faces;
	};

	// 浮点数极小值常量，用于防止除零和精度问题
	constexpr float EPS = 1e-12f;

	// ===================================================================
	// 三维向量与数学辅助函数
	// ===================================================================

	// 向量加法
	static Point Add(const Point& a, const Point& b) { return { a.x + b.x, a.y + b.y, a.z + b.z, -1 }; }
	// 向量减法
	static Point Sub(const Point& a, const Point& b) { return { a.x - b.x, a.y - b.y, a.z - b.z, -1 }; }
	// 向量标量乘法
	static Point Mul(const Point& a, float s) { return { a.x * s, a.y * s, a.z * s, -1 }; }
	// 向量点乘
	static float Dot(const Point& a, const Point& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
	// 向量叉乘
	static Point Cross(const Point& a, const Point& b)
	{
		return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x, -1 };
	}
	// 计算向量长度 (模)
	static float Length(const Point& a) { return std::sqrt(Dot(a, a)); }
	// 计算两点之间的欧氏距离
	static float Distance(const Point& a, const Point& b) { return Length(Sub(a, b)); }

	// ===================================================================
	// 几何特征提取逻辑
	// ===================================================================

	// 将共面的三角面片加入到对应的轴向组中，实现墙面聚类
	static void AddTriangleToPlaneGroup(std::vector<AxisPlaneGroup>& groups, char axis, float coord, const Triangle& tri, float merge_tol)
	{
		for (auto& g : groups)
		{
			// 如果当前坐标与已有组的坐标差距在容差内，则认为是同一面墙
			if (std::fabs(g.plane_coord - coord) <= merge_tol)
			{
				g.faces.push_back(tri);
				// 更新该面墙的平均坐标
				const int count = static_cast<int>(g.faces.size());
				g.plane_coord = (g.plane_coord * (count - 1) + coord) / static_cast<float>(count);
				return;
			}
		}
		// 若无法匹配任何组，则新建一个平面组
		AxisPlaneGroup group;
		group.axis = axis;
		group.plane_coord = coord;
		group.faces.push_back(tri);
		groups.push_back(std::move(group));
	}

	// 提取同一面墙组中的极端边界值（最大或最小值）
	static float ExtractAxisExtremeFromGroup(const AxisPlaneGroup& group, bool take_min)
	{
		float value = take_min ? std::numeric_limits<float>::infinity() : -std::numeric_limits<float>::infinity();
		auto update_val = [&](float v)
			{ value = take_min ? std::min(value, v) : std::max(value, v); };

		for (const auto& tri : group.faces)
		{
			if (group.axis == 'X')
			{
				update_val(tri.v0.x);
				update_val(tri.v1.x);
				update_val(tri.v2.x);
			}
			else if (group.axis == 'Y')
			{
				update_val(tri.v0.y);
				update_val(tri.v1.y);
				update_val(tri.v2.y);
			}
			else
			{
				update_val(tri.v0.z);
				update_val(tri.v1.z);
				update_val(tri.v2.z);
			}
		}
		if (!std::isfinite(value))
			throw std::runtime_error("Failed to extract valid boundary value from STL wall group.");
		return value;
	}

	// 核心预处理：从混乱的 STL 网格中恢复出正交的房间包围盒
	static RoomBox ExtractRoomBoxFromStl(const std::vector<Triangle>& stl_mesh)
	{
		if (stl_mesh.empty())
			throw std::runtime_error("STL mesh is empty. Cannot recover room box.");

		// 法线对齐容差与平面合并容差
		const float normal_tol = 0.90f, plane_merge_tol = 1e-4f;
		std::vector<AxisPlaneGroup> x_groups, y_groups, z_groups;
		x_groups.reserve(8);
		y_groups.reserve(8);
		z_groups.reserve(8);

		// 遍历面片，通过法线方向对墙面进行分类 (X墙, Y墙, Z墙/天花板地板)
		for (const auto& tri : stl_mesh)
		{
			Point e1 = Sub(tri.v1, tri.v0), e2 = Sub(tri.v2, tri.v0);
			Point n = Cross(e1, e2);
			float len = Length(n);
			if (len < EPS)
				continue;			// 剔除退化的三角形
			n = Mul(n, 1.0f / len); // 法线归一化

			float ax = std::fabs(n.x), ay = std::fabs(n.y), az = std::fabs(n.z);

			// 根据最占优的法线分量，将面片分到对应的轴平面组
			if (ax >= ay && ax >= az && ax >= normal_tol)
			{
				AddTriangleToPlaneGroup(x_groups, 'X', (tri.v0.x + tri.v1.x + tri.v2.x) / 3.0f, tri, plane_merge_tol);
			}
			else if (ay >= ax && ay >= az && ay >= normal_tol)
			{
				AddTriangleToPlaneGroup(y_groups, 'Y', (tri.v0.y + tri.v1.y + tri.v2.y) / 3.0f, tri, plane_merge_tol);
			}
			else if (az >= ax && az >= ay && az >= normal_tol)
			{
				AddTriangleToPlaneGroup(z_groups, 'Z', (tri.v0.z + tri.v1.z + tri.v2.z) / 3.0f, tri, plane_merge_tol);
			}
		}

		if (x_groups.size() < 2 || y_groups.size() < 2 || z_groups.size() < 2)
			throw std::runtime_error("Failed to recover 6 main walls from STL.");

		// 分别找出 X, Y, Z 三个方向上的极限平面 (即房间的最小墙和最大墙)
		auto cmp = [](const AxisPlaneGroup& a, const AxisPlaneGroup& b)
			{ return a.plane_coord < b.plane_coord; };
		auto x_min_it = std::min_element(x_groups.begin(), x_groups.end(), cmp);
		auto x_max_it = std::max_element(x_groups.begin(), x_groups.end(), cmp);
		auto y_min_it = std::min_element(y_groups.begin(), y_groups.end(), cmp);
		auto y_max_it = std::max_element(y_groups.begin(), y_groups.end(), cmp);
		auto z_min_it = std::min_element(z_groups.begin(), z_groups.end(), cmp);
		auto z_max_it = std::max_element(z_groups.begin(), z_groups.end(), cmp);

		// 构建并返回准确的房间长方体边界
		RoomBox box{
			ExtractAxisExtremeFromGroup(*x_min_it, true), ExtractAxisExtremeFromGroup(*x_max_it, false),
			ExtractAxisExtremeFromGroup(*y_min_it, true), ExtractAxisExtremeFromGroup(*y_max_it, false),
			ExtractAxisExtremeFromGroup(*z_min_it, true), ExtractAxisExtremeFromGroup(*z_max_it, false) };
		return box;
	}

	// 利用包围盒构建房间的 8 个角点
	static std::vector<Point> BuildCorners(const RoomBox& box)
	{
		return {
			{box.min_x, box.min_y, box.min_z, -1}, {box.min_x, box.min_y, box.max_z, -1}, {box.min_x, box.max_y, box.min_z, -1}, {box.min_x, box.max_y, box.max_z, -1}, {box.max_x, box.min_y, box.min_z, -1}, {box.max_x, box.min_y, box.max_z, -1}, {box.max_x, box.max_y, box.min_z, -1}, {box.max_x, box.max_y, box.max_z, -1} };
	}

	// 利用包围盒构建房间的 12 条棱 (边缘)
	static std::vector<Segment3> BuildEdges(const RoomBox& box)
	{
		auto c = BuildCorners(box);
		return {
			{c[0], c[2]}, {c[2], c[6]}, {c[6], c[4]}, {c[4], c[0]}, {c[1], c[3]}, {c[3], c[7]}, {c[7], c[5]}, {c[5], c[1]}, {c[0], c[1]}, {c[2], c[3]}, {c[6], c[7]}, {c[4], c[5]} };
	}

	// 计算给定点到房间 6 面墙的最短距离 (用于 GNN 物理先验特征)
	static float ComputeWallDistance(const Point& p, const RoomBox& box)
	{
		return std::min({ std::fabs(p.x - box.min_x), std::fabs(box.max_x - p.x),
						 std::fabs(p.y - box.min_y), std::fabs(box.max_y - p.y),
						 std::fabs(p.z - box.min_z), std::fabs(box.max_z - p.z) });
	}

	// 计算给定点到房间 8 个角落的最短距离
	static float ComputeMinCornerDistance(const Point& p, const std::vector<Point>& corners)
	{
		float min_dist = std::numeric_limits<float>::max();
		for (const auto& c : corners)
			min_dist = std::min(min_dist, Distance(p, c));
		return min_dist;
	}

	// 内部函数：计算点到空间任意线段的最短距离
	static float ComputePointSegmentDistance(const Point& p, const Segment3& seg)
	{
		Point ab = Sub(seg.b, seg.a), ap = Sub(p, seg.a);
		float ab2 = Dot(ab, ab);
		if (ab2 < EPS)
			return Distance(p, seg.a);
		float t = std::max(0.0f, std::min(1.0f, Dot(ap, ab) / ab2));
		return Distance(p, Add(seg.a, Mul(ab, t)));
	}

	// 计算给定点到房间 12 条边缘的最短距离
	static float ComputeMinEdgeDistance(const Point& p, const std::vector<Segment3>& edges)
	{
		float min_dist = std::numeric_limits<float>::max();
		for (const auto& e : edges)
			min_dist = std::min(min_dist, ComputePointSegmentDistance(p, e));
		return min_dist;
	}

	// ===================================================================
	// 解析引擎：Windows 原生内存映射 (mmap) 与 C++17 std::from_chars
	// ===================================================================
	class MmapScanner
	{
		const char* mapped_data = nullptr; // 指向内存映射区域首地址的指针
		size_t file_size = 0;			   // 文件总字节数
		const char* cur = nullptr;		   // 当前扫描游标
		const char* end = nullptr;		   // 内存映射区域尾指针

		HANDLE hFile = INVALID_HANDLE_VALUE; // Windows 文件句柄
		HANDLE hMap = NULL;					 // Windows 内存映射对象句柄

	public:
		MmapScanner(const char* filepath)
		{
			// 1. 调用 Win32 API 打开物理文件，获取读取权限
			hFile = CreateFileA(filepath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
			if (hFile == INVALID_HANDLE_VALUE)
				return;

			// 2. 获取文件真实大小，支持超过 4GB 的超大文件
			LARGE_INTEGER size;
			if (!GetFileSizeEx(hFile, &size) || size.QuadPart == 0)
				return;
			file_size = size.QuadPart;

			// 3. 创建文件映射内核对象 (只读)
			hMap = CreateFileMappingA(hFile, NULL, PAGE_READONLY, 0, 0, NULL);
			if (!hMap)
				return;

			// 4. 将文件视图直接映射到本进程的虚拟地址空间，实现零拷贝 (Zero-copy) 访问
			mapped_data = static_cast<const char*>(MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0));
			if (mapped_data)
			{
				cur = mapped_data;
				end = mapped_data + file_size;
			}
		}

		~MmapScanner()
		{
			// 析构时必须依次解绑并关闭 Windows 内核句柄，防止资源泄露
			if (mapped_data)
				UnmapViewOfFile(mapped_data);
			if (hMap)
				CloseHandle(hMap);
			if (hFile != INVALID_HANDLE_VALUE)
				CloseHandle(hFile);
		}

		// 检查内存映射是否成功
		bool is_open() const { return mapped_data != nullptr; }

		// 极速提取下一个浮点数 (替代原有的繁琐状态机)
		bool next_float(float& val)
		{
			// 跳过前置的空格、换行符和制表符
			while (cur < end && std::isspace(static_cast<unsigned char>(*cur)))
			{
				cur++;
			}
			if (cur >= end)
				return false;

			// 补丁：C++17 std::from_chars 标准不支持带 '+' 前缀的浮点数，需要手动跳过
			if (*cur == '+')
				cur++;

			// 调用 C++17 底层极致优化的字符转换库，无视 Locale，且不会抛出异常
			auto [ptr, ec] = std::from_chars(cur, end, val);

			if (ec == std::errc())
			{
				cur = ptr; // 转换成功，游标前进到数值的末尾
				return true;
			}
			return false;
		}

		// 跳过当前行的剩余字符，直至遇到换行符
		bool skip_line()
		{
			while (cur < end && *cur != '\n')
			{
				cur++;
			}
			if (cur < end && *cur == '\n')
			{
				cur++;
				return true;
			}
			return false;
		}
	};
}

// ===================================================================
// 对外接口实现
// ===================================================================

// 解析 ASCII 格式的 STL 文件
std::vector<Triangle> ParseSTL(const char* filepath)
{
	std::vector<Triangle> mesh;
	std::ifstream file(filepath, std::ios::binary);
	if (!file.is_open())
	{
		PRINT_ERR << "[Error] Failed to open STL file: " << filepath << std::endl;
		return mesh;
	}

	char header[80];
	file.read(header, 80);
	uint32_t num_triangles = 0;
	file.read(reinterpret_cast<char*>(&num_triangles), sizeof(uint32_t));

	if (num_triangles == 0)
		return mesh;

	mesh.reserve(num_triangles);
	float normal[3], v0[3], v1[3], v2[3];
	uint16_t attribute_byte_count = 0;

	for (uint32_t i = 0; i < num_triangles; ++i)
	{
		file.read(reinterpret_cast<char*>(normal), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v0), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v1), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v2), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(&attribute_byte_count), sizeof(uint16_t));

		// 读入顶点时顺便做毫米到米的单位换算
		mesh.push_back({ {v0[0] * 0.001f, v0[1] * 0.001f, v0[2] * 0.001f, -1},
						{v1[0] * 0.001f, v1[1] * 0.001f, v1[2] * 0.001f, -1},
						{v2[0] * 0.001f, v2[1] * 0.001f, v2[2] * 0.001f, -1} });
	}
	file.close();
	return mesh;
}

// 经过深度优化的单次解析核心逻辑 (Single-Pass Core)
bool ParseTrhist(const char* filepath, const std::vector<Triangle>& stl_mesh,
	std::vector<Point>& nodes, std::vector<float>& node_features, std::vector<float>& node_attrs,
	int& num_nodes, int& time_steps, int& feature_dim, int& attr_dim,
	float Xc, float Yc, float Zc, float W)
{
	// 启动高性能 Windows Mmap 扫描器
	MmapScanner scanner(filepath);

	if (!scanner.is_open())
	{
		PRINT_ERR << "[Error] Failed to open trhist file via Memory Mapping: " << filepath << std::endl;
		return false;
	}
	if (stl_mesh.empty())
	{
		PRINT_ERR << "[Error] STL mesh is empty." << std::endl;
		return false;
	}

	// 高速跳过 Trhist 的无关头部数据 (Line 1 ~ Line 5)
	scanner.skip_line();
	float f_nn, f_nv;
	if (!scanner.next_float(f_nn) || !scanner.next_float(f_nv))
		return false;
	num_nodes = static_cast<int>(f_nn); // 获取关键的节点总数

	scanner.skip_line(); // 跳过行：节点数 属性数
	scanner.skip_line(); // 跳过行：       x       y       z      vx      vy      vz
	scanner.skip_line(); // 跳过行：      sx      sy      sz     sxy     syz     szx
	scanner.skip_line(); // 跳过行：     efp     rho    rvol  active

	feature_dim = 5; // 节点动态特征维度: rho, vx, vy, vz, pressure
	attr_dim = 11;	 // 节点静态属性维度: x, y, z, 爆炸距离d, nx, ny, nz, 当量W, 以及到墙/边/角的最短距离

	// 构建物理房间的三维边界先验
	RoomBox room_box;
	std::vector<Point> corners;
	std::vector<Segment3> edges;
	try
	{
		room_box = ExtractRoomBoxFromStl(stl_mesh);
		corners = BuildCorners(room_box);
		edges = BuildEdges(room_box);
	}
	catch (const std::exception& ex)
	{
		PRINT_ERR << "[Error] Failed to build room geometry prior: " << ex.what() << std::endl;
		return false;
	}

	// 预分配内存，防止 Vector 扩容带来的拷贝开销
	nodes.clear();
	nodes.reserve(num_nodes);
	node_attrs.clear();
	node_attrs.reserve(static_cast<size_t>(num_nodes) * attr_dim);

	const float W_cbrt = std::cbrt(W);
	const float Xc_m = Xc * 0.001f, Yc_m = Yc * 0.001f, Zc_m = Zc * 0.001f;

	time_steps = 0;

	// 为单次遍历准备 Time-Major 存储 (T, N, D)，极其有利于缓存连续写入
	std::vector<float> time_major_features;
	time_major_features.reserve(static_cast<size_t>(num_nodes) * 100 * feature_dim);

	// Phase 1: 边读边析 (Stream parsing into time-major layout)
	while (true)
	{
		float time_val;
		// 如果读不到时间步浮点数，说明文件结束
		if (!scanner.next_float(time_val))
			break;

		for (int i = 0; i < num_nodes; ++i)
		{
			float x, y, z, vx, vy, vz, sx, sy, sz, sxy, syz, szx, efp, rho, rvol, active;

			// 利用超快的 std::from_chars 连续提取一行内所有的流场变量
			if (!scanner.next_float(x) || !scanner.next_float(y) || !scanner.next_float(z) ||
				!scanner.next_float(vx) || !scanner.next_float(vy) || !scanner.next_float(vz) ||
				!scanner.next_float(sx) || !scanner.next_float(sy) || !scanner.next_float(sz) ||
				!scanner.next_float(sxy) || !scanner.next_float(syz) || !scanner.next_float(szx) ||
				!scanner.next_float(efp) || !scanner.next_float(rho) || !scanner.next_float(rvol) ||
				!scanner.next_float(active))
			{
				PRINT_ERR << "[Error] Parsing aborted at step " << time_steps << ", node " << i << std::endl;
				return false;
			}

			// LS-DYNA 应力张量主对角线均值的负数 即为压力
			float raw_pressure = -(sx + sy + sz) / 3.0f;

			// 在第 0 个时间步，初始化该节点的空间拓扑以及所有静态属性
			if (time_steps == 0)
			{
				Point p{ x * 0.001f, y * 0.001f, z * 0.001f, i };
				nodes.push_back(p);

				float dx = p.x - Xc_m, dy = p.y - Yc_m, dz = p.z - Zc_m;
				// 节点到爆心的距离
				float d = std::sqrt(dx * dx + dy * dy + dz * dz);

				node_attrs.push_back(p.x);
				node_attrs.push_back(p.y);
				node_attrs.push_back(p.z);
				node_attrs.push_back(d);
				node_attrs.push_back(d > 1e-6f ? dx / d : 0.0f);
				node_attrs.push_back(d > 1e-6f ? dy / d : 0.0f);
				node_attrs.push_back(d > 1e-6f ? dz / d : 1.0f);
				node_attrs.push_back(W_cbrt);

				// 注入边界距离先验
				node_attrs.push_back(ComputeWallDistance(p, room_box));
				node_attrs.push_back(ComputeMinEdgeDistance(p, edges));
				node_attrs.push_back(ComputeMinCornerDistance(p, corners));
			}

			// 保存当前时间步的物理特征
			time_major_features.push_back(rho);
			time_major_features.push_back(vx * 0.001f);
			time_major_features.push_back(vy * 0.001f);
			time_major_features.push_back(vz * 0.001f);
			time_major_features.push_back(raw_pressure); // 此时暂存未修正的原始压力
		}
		time_steps++;
	}

	if (time_steps <= 0)
		return false;

	// Phase 2: OpenMP 并行矩阵转置 (将数据由 T-Major 转为 N-Major，符合 GNN Pytorch 布局习惯)
	node_features.resize(static_cast<size_t>(num_nodes) * time_steps * feature_dim, 0.0f);

#pragma omp parallel for
	for (int i = 0; i < num_nodes; ++i)
	{
		for (int t = 0; t < time_steps; ++t)
		{
			size_t src_base = (static_cast<size_t>(t) * num_nodes + i) * feature_dim;
			size_t dst_base = (static_cast<size_t>(i) * time_steps + t) * feature_dim;

			node_features[dst_base + 0] = time_major_features[src_base + 0];
			node_features[dst_base + 1] = time_major_features[src_base + 1];
			node_features[dst_base + 2] = time_major_features[src_base + 2];
			node_features[dst_base + 3] = time_major_features[src_base + 3];
			node_features[dst_base + 4] = time_major_features[src_base + 4];
		}
	}

	PRINT_LOG << "[Info] Single-Pass parsed trhist via Windows Mmap. Nodes: " << num_nodes << ", Time steps: " << time_steps << std::endl;
	return true;
}