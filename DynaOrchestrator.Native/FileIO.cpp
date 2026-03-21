//
// Created by tianjia on 2026/3/17.
//

#include "FileIO.h"
#include "Logger.h"

#include <string>
#include <fstream>
#include <sstream>
#include <iostream>
#include <algorithm>
#include <cmath>
#include <limits>
#include <cctype>
#include <stdexcept>

namespace
{
	struct RoomBox
	{
		float min_x, max_x;
		float min_y, max_y;
		float min_z, max_z;
	};

	struct Segment3
	{
		Point a;
		Point b;
	};

	struct AxisPlaneGroup
	{
		char axis;					 // 'X' / 'Y' / 'Z'
		float plane_coord;			 // 墙面坐标，如 x = const
		std::vector<Triangle> faces; // 属于该墙面的三角面
	};

	constexpr float EPS = 1e-12f;

	static Point Add(const Point &a, const Point &b)
	{
		return {a.x + b.x, a.y + b.y, a.z + b.z, -1};
	}

	static Point Sub(const Point &a, const Point &b)
	{
		return {a.x - b.x, a.y - b.y, a.z - b.z, -1};
	}

	static Point Mul(const Point &a, float s)
	{
		return {a.x * s, a.y * s, a.z * s, -1};
	}

	static float Dot(const Point &a, const Point &b)
	{
		return a.x * b.x + a.y * b.y + a.z * b.z;
	}

	static Point Cross(const Point &a, const Point &b)
	{
		return {
			a.y * b.z - a.z * b.y,
			a.z * b.x - a.x * b.z,
			a.x * b.y - a.y * b.x,
			-1};
	}

	static float Length(const Point &a)
	{
		return std::sqrt(Dot(a, a));
	}

	static float Distance(const Point &a, const Point &b)
	{
		return Length(Sub(a, b));
	}

	static void AddTriangleToPlaneGroup(
		std::vector<AxisPlaneGroup> &groups,
		char axis,
		float coord,
		const Triangle &tri,
		float merge_tol)
	{
		for (auto &g : groups)
		{
			if (std::fabs(g.plane_coord - coord) <= merge_tol)
			{
				g.faces.push_back(tri);
				const int count = static_cast<int>(g.faces.size());
				g.plane_coord = (g.plane_coord * (count - 1) + coord) / static_cast<float>(count);
				return;
			}
		}

		AxisPlaneGroup group;
		group.axis = axis;
		group.plane_coord = coord;
		group.faces.push_back(tri);
		groups.push_back(std::move(group));
	}

	static float ExtractAxisExtremeFromGroup(const AxisPlaneGroup &group, bool take_min)
	{
		float value = take_min
						  ? std::numeric_limits<float>::infinity()
						  : -std::numeric_limits<float>::infinity();

		auto update_val = [&](float v)
		{
			if (take_min)
				value = std::min(value, v);
			else
				value = std::max(value, v);
		};

		for (const auto &tri : group.faces)
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

	static RoomBox ExtractRoomBoxFromStl(const std::vector<Triangle> &stl_mesh)
	{
		if (stl_mesh.empty())
			throw std::runtime_error("STL mesh is empty. Cannot recover room box.");

		const float normal_tol = 0.90f;
		const float plane_merge_tol = 1e-4f;

		std::vector<AxisPlaneGroup> x_groups;
		std::vector<AxisPlaneGroup> y_groups;
		std::vector<AxisPlaneGroup> z_groups;

		x_groups.reserve(8);
		y_groups.reserve(8);
		z_groups.reserve(8);

		for (const auto &tri : stl_mesh)
		{
			Point e1 = Sub(tri.v1, tri.v0);
			Point e2 = Sub(tri.v2, tri.v0);
			Point n = Cross(e1, e2);
			float len = Length(n);
			if (len < EPS)
				continue;

			n = Mul(n, 1.0f / len);

			float ax = std::fabs(n.x);
			float ay = std::fabs(n.y);
			float az = std::fabs(n.z);

			if (ax >= ay && ax >= az && ax >= normal_tol)
			{
				float coord = (tri.v0.x + tri.v1.x + tri.v2.x) / 3.0f;
				AddTriangleToPlaneGroup(x_groups, 'X', coord, tri, plane_merge_tol);
			}
			else if (ay >= ax && ay >= az && ay >= normal_tol)
			{
				float coord = (tri.v0.y + tri.v1.y + tri.v2.y) / 3.0f;
				AddTriangleToPlaneGroup(y_groups, 'Y', coord, tri, plane_merge_tol);
			}
			else if (az >= ax && az >= ay && az >= normal_tol)
			{
				float coord = (tri.v0.z + tri.v1.z + tri.v2.z) / 3.0f;
				AddTriangleToPlaneGroup(z_groups, 'Z', coord, tri, plane_merge_tol);
			}
		}

		if (x_groups.size() < 2 || y_groups.size() < 2 || z_groups.size() < 2)
		{
			std::ostringstream oss;
			oss << "Failed to recover 6 main walls from STL. "
				<< "X groups = " << x_groups.size()
				<< ", Y groups = " << y_groups.size()
				<< ", Z groups = " << z_groups.size();
			throw std::runtime_error(oss.str());
		}

		auto x_min_it = std::min_element(x_groups.begin(), x_groups.end(),
										 [](const AxisPlaneGroup &a, const AxisPlaneGroup &b)
										 {
											 return a.plane_coord < b.plane_coord;
										 });
		auto x_max_it = std::max_element(x_groups.begin(), x_groups.end(),
										 [](const AxisPlaneGroup &a, const AxisPlaneGroup &b)
										 {
											 return a.plane_coord < b.plane_coord;
										 });
		auto y_min_it = std::min_element(y_groups.begin(), y_groups.end(),
										 [](const AxisPlaneGroup &a, const AxisPlaneGroup &b)
										 {
											 return a.plane_coord < b.plane_coord;
										 });
		auto y_max_it = std::max_element(y_groups.begin(), y_groups.end(),
										 [](const AxisPlaneGroup &a, const AxisPlaneGroup &b)
										 {
											 return a.plane_coord < b.plane_coord;
										 });
		auto z_min_it = std::min_element(z_groups.begin(), z_groups.end(),
										 [](const AxisPlaneGroup &a, const AxisPlaneGroup &b)
										 {
											 return a.plane_coord < b.plane_coord;
										 });
		auto z_max_it = std::max_element(z_groups.begin(), z_groups.end(),
										 [](const AxisPlaneGroup &a, const AxisPlaneGroup &b)
										 {
											 return a.plane_coord < b.plane_coord;
										 });

		RoomBox box{
			ExtractAxisExtremeFromGroup(*x_min_it, true),
			ExtractAxisExtremeFromGroup(*x_max_it, false),
			ExtractAxisExtremeFromGroup(*y_min_it, true),
			ExtractAxisExtremeFromGroup(*y_max_it, false),
			ExtractAxisExtremeFromGroup(*z_min_it, true),
			ExtractAxisExtremeFromGroup(*z_max_it, false)};

		if (!(box.max_x > box.min_x && box.max_y > box.min_y && box.max_z > box.min_z))
		{
			std::ostringstream oss;
			oss << "Recovered room box is invalid: "
				<< "X[" << box.min_x << ", " << box.max_x << "], "
				<< "Y[" << box.min_y << ", " << box.max_y << "], "
				<< "Z[" << box.min_z << ", " << box.max_z << "]";
			throw std::runtime_error(oss.str());
		}

		return box;
	}

	static std::vector<Point> BuildCorners(const RoomBox &box)
	{
		return {
			{box.min_x, box.min_y, box.min_z, -1},
			{box.min_x, box.min_y, box.max_z, -1},
			{box.min_x, box.max_y, box.min_z, -1},
			{box.min_x, box.max_y, box.max_z, -1},
			{box.max_x, box.min_y, box.min_z, -1},
			{box.max_x, box.min_y, box.max_z, -1},
			{box.max_x, box.max_y, box.min_z, -1},
			{box.max_x, box.max_y, box.max_z, -1}};
	}

	static std::vector<Segment3> BuildEdges(const RoomBox &box)
	{
		auto c = BuildCorners(box);

		return {
			{c[0], c[2]},
			{c[2], c[6]},
			{c[6], c[4]},
			{c[4], c[0]},

			{c[1], c[3]},
			{c[3], c[7]},
			{c[7], c[5]},
			{c[5], c[1]},

			{c[0], c[1]},
			{c[2], c[3]},
			{c[6], c[7]},
			{c[4], c[5]}};
	}

	static float ComputeWallDistance(const Point &p, const RoomBox &box)
	{
		float dx1 = std::fabs(p.x - box.min_x);
		float dx2 = std::fabs(box.max_x - p.x);
		float dy1 = std::fabs(p.y - box.min_y);
		float dy2 = std::fabs(box.max_y - p.y);
		float dz1 = std::fabs(p.z - box.min_z);
		float dz2 = std::fabs(box.max_z - p.z);

		return std::min(
			std::min(std::min(dx1, dx2), std::min(dy1, dy2)),
			std::min(dz1, dz2));
	}

	static float ComputeMinCornerDistance(const Point &p, const std::vector<Point> &corners)
	{
		float min_dist = std::numeric_limits<float>::max();
		for (const auto &c : corners)
			min_dist = std::min(min_dist, Distance(p, c));
		return min_dist;
	}

	static float ComputePointSegmentDistance(const Point &p, const Segment3 &seg)
	{
		Point ab = Sub(seg.b, seg.a);
		Point ap = Sub(p, seg.a);

		float ab2 = Dot(ab, ab);
		if (ab2 < EPS)
			return Distance(p, seg.a);

		float t = Dot(ap, ab) / ab2;
		t = std::max(0.0f, std::min(1.0f, t));

		Point proj = Add(seg.a, Mul(ab, t));
		return Distance(p, proj);
	}

	static float ComputeMinEdgeDistance(const Point &p, const std::vector<Segment3> &edges)
	{
		float min_dist = std::numeric_limits<float>::max();
		for (const auto &e : edges)
			min_dist = std::min(min_dist, ComputePointSegmentDistance(p, e));
		return min_dist;
	}
}

// 解析 Binary 格式 STL
std::vector<Triangle> ParseSTL(const char *filepath)
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
	file.read(reinterpret_cast<char *>(&num_triangles), sizeof(uint32_t));

	if (num_triangles == 0)
	{
		PRINT_ERR << "[Warning] STL file contains 0 triangles." << std::endl;
		return mesh;
	}

	mesh.reserve(num_triangles);

	float normal[3];
	float v0[3], v1[3], v2[3];
	uint16_t attribute_byte_count = 0;

	for (uint32_t i = 0; i < num_triangles; ++i)
	{
		file.read(reinterpret_cast<char *>(normal), 3 * sizeof(float));
		file.read(reinterpret_cast<char *>(v0), 3 * sizeof(float));
		file.read(reinterpret_cast<char *>(v1), 3 * sizeof(float));
		file.read(reinterpret_cast<char *>(v2), 3 * sizeof(float));
		file.read(reinterpret_cast<char *>(&attribute_byte_count), sizeof(uint16_t));

		Triangle tri;
		tri.v0 = {v0[0] * 0.001f, v0[1] * 0.001f, v0[2] * 0.001f, -1};
		tri.v1 = {v1[0] * 0.001f, v1[1] * 0.001f, v1[2] * 0.001f, -1};
		tri.v2 = {v2[0] * 0.001f, v2[1] * 0.001f, v2[2] * 0.001f, -1};

		mesh.push_back(tri);
	}

	file.close();

	PRINT_LOG << "[Info] Successfully parsed Binary STL. Total triangles: " << mesh.size() << std::endl;
	return mesh;
}

static inline void TrimString(std::string &s)
{
	s.erase(s.begin(), std::find_if(s.begin(), s.end(), [](unsigned char ch)
									{ return !std::isspace(ch); }));

	s.erase(std::find_if(s.rbegin(), s.rend(), [](unsigned char ch)
						 { return !std::isspace(ch); })
				.base(),
			s.end());
}

bool ParseTrhist(const char *filepath,
				 const std::vector<Triangle> &stl_mesh,
				 std::vector<Point> &nodes,
				 std::vector<float> &node_features,
				 std::vector<float> &node_attrs,
				 int &num_nodes, int &time_steps, int &feature_dim,
				 int &attr_dim,
				 float Xc, float Yc, float Zc, float W)
{
	std::ifstream file(filepath);
	if (!file.is_open())
	{
		PRINT_ERR << "[Error] Failed to open trhist file: " << filepath << std::endl;
		return false;
	}

	if (stl_mesh.empty())
	{
		PRINT_ERR << "[Error] STL mesh is empty. node_attr(11) cannot be constructed." << std::endl;
		return false;
	}

	std::string line;

	// 读取头部
	std::getline(file, line);
	std::getline(file, line);

	std::istringstream header_iss(line);
	int num_vars = 0;
	header_iss >> num_nodes >> num_vars;

	// 跳过列说明
	std::getline(file, line);
	std::getline(file, line);
	std::getline(file, line);

	feature_dim = 5;
	attr_dim = 11;

	RoomBox room_box;
	std::vector<Point> corners;
	std::vector<Segment3> edges;

	try
	{
		room_box = ExtractRoomBoxFromStl(stl_mesh);
		corners = BuildCorners(room_box);
		edges = BuildEdges(room_box);
	}
	catch (const std::exception &ex)
	{
		PRINT_ERR << "[Error] Failed to build room geometry prior from STL: " << ex.what() << std::endl;
		file.close();
		return false;
	}

	nodes.clear();
	nodes.reserve(num_nodes);

	node_attrs.clear();
	node_attrs.reserve(static_cast<size_t>(num_nodes) * attr_dim);

	const float W_cbrt = std::cbrt(W);

	const float Xc_m = Xc * 0.001f;
	const float Yc_m = Yc * 0.001f;
	const float Zc_m = Zc * 0.001f;

	time_steps = 0;
	bool first_step_processed = false;

	double p0_sum = 0.0;
	int p0_count = 0;
	float P0 = 0.0f;

	// 第一遍：统计 time_steps，提取节点和静态属性，估计 P0
	while (std::getline(file, line))
	{
		TrimString(line);
		if (line.empty())
			continue;

		try
		{
			(void)std::stof(line);
		}
		catch (...)
		{
			break;
		}

		time_steps++;

		for (int i = 0; i < num_nodes; ++i)
		{
			std::string lineA, lineB, lineC;

			// 必须检查三行是否真的读到了
			if (!std::getline(file, lineA) || !std::getline(file, lineB) || !std::getline(file, lineC))
			{
				PRINT_ERR << "[Error] Unexpected EOF in ParseTrhist first pass at time_step="
						  << (time_steps - 1) << ", node=" << i << std::endl;
				file.close();
				return false;
			}

			double x_d, y_d, z_d, vx_d, vy_d, vz_d;
			double sx_d, sy_d, sz_d, sxy_d, syz_d, szx_d;
			double efp_d, rho_d, rvol_d;
			int active;

			std::istringstream issA(lineA), issB(lineB), issC(lineC);

			// 必须检查三行数字是否解析成功，避免脏值进入后续计算
			if (!(issA >> x_d >> y_d >> z_d >> vx_d >> vy_d >> vz_d) ||
				!(issB >> sx_d >> sy_d >> sz_d >> sxy_d >> syz_d >> szx_d) ||
				!(issC >> efp_d >> rho_d >> rvol_d >> active))
			{
				PRINT_ERR << "[Error] Failed to parse trhist numeric fields in first pass at time_step="
						  << (time_steps - 1) << ", node=" << i << std::endl;

				PRINT_ERR << "[Error] lineA = [" << lineA << "]" << std::endl;
				PRINT_ERR << "[Error] lineB = [" << lineB << "]" << std::endl;
				PRINT_ERR << "[Error] lineC = [" << lineC << "]" << std::endl;

				file.close();
				return false;
			}

			const float x = static_cast<float>(x_d);
			const float y = static_cast<float>(y_d);
			const float z = static_cast<float>(z_d);
			const float vx = static_cast<float>(vx_d);
			const float vy = static_cast<float>(vy_d);
			const float vz = static_cast<float>(vz_d);

			const float sx = static_cast<float>(sx_d);
			const float sy = static_cast<float>(sy_d);
			const float sz = static_cast<float>(sz_d);
			const float sxy = static_cast<float>(sxy_d);
			const float syz = static_cast<float>(syz_d);
			const float szx = static_cast<float>(szx_d);

			const float efp = static_cast<float>(efp_d);
			const float rho = static_cast<float>(rho_d);
			const float rvol = static_cast<float>(rvol_d);

			if (!first_step_processed)
			{
				Point p;
				p.x = x * 0.001f;
				p.y = y * 0.001f;
				p.z = z * 0.001f;
				p.id = i;
				nodes.push_back(p);

				const float dx = p.x - Xc_m;
				const float dy = p.y - Yc_m;
				const float dz = p.z - Zc_m;
				const float d = std::sqrt(dx * dx + dy * dy + dz * dz);

				const float nx = (d > 1e-6f) ? (dx / d) : 0.0f;
				const float ny = (d > 1e-6f) ? (dy / d) : 0.0f;
				const float nz = (d > 1e-6f) ? (dz / d) : 1.0f;

				const float d_wall = ComputeWallDistance(p, room_box);
				const float d_edge = ComputeMinEdgeDistance(p, edges);
				const float d_corner = ComputeMinCornerDistance(p, corners);

				node_attrs.push_back(p.x);
				node_attrs.push_back(p.y);
				node_attrs.push_back(p.z);
				node_attrs.push_back(d);
				node_attrs.push_back(nx);
				node_attrs.push_back(ny);
				node_attrs.push_back(nz);
				node_attrs.push_back(W_cbrt);
				node_attrs.push_back(d_wall);
				node_attrs.push_back(d_edge);
				node_attrs.push_back(d_corner);

				const float raw_pressure = -(sx + sy + sz) / 3.0f;

				if (std::isfinite(raw_pressure) && std::isfinite(rho) && std::isfinite(rvol) &&
					rho > 0.0f && rvol > 0.0f)
				{
					p0_sum += raw_pressure;
					p0_count++;
				}
			}
		}

		if (!first_step_processed)
		{
			first_step_processed = true;

			if (p0_count <= 0)
			{
				PRINT_ERR << "[Error] Failed to estimate ambient pressure P0 from first time step." << std::endl;
				file.close();
				return false;
			}

			P0 = static_cast<float>(p0_sum / static_cast<double>(p0_count));
		}
	}

	file.close();

	if (time_steps <= 0)
	{
		PRINT_ERR << "[Error] No valid time steps found in trhist file." << std::endl;
		return false;
	}

	// 预分配特征数组，布局 (N, T, D)
	node_features.clear();
	node_features.resize(static_cast<size_t>(num_nodes) * time_steps * feature_dim, 0.0f);

	// 第二遍：写 node_features
	std::ifstream file2(filepath);
	if (!file2.is_open())
	{
		PRINT_ERR << "[Error] Failed to reopen trhist file: " << filepath << std::endl;
		return false;
	}

	std::getline(file2, line);
	std::getline(file2, line);

	std::getline(file2, line);
	std::getline(file2, line);
	std::getline(file2, line);

	int t_idx = 0;

	while (std::getline(file2, line))
	{
		TrimString(line);
		if (line.empty())
			continue;

		try
		{
			(void)std::stof(line);
		}
		catch (...)
		{
			break;
		}

		// 防止第一遍统计和第二遍实际读取不一致
		if (t_idx >= time_steps)
		{
			PRINT_ERR << "[Error] t_idx out of range in ParseTrhist second pass. t_idx="
					  << t_idx << ", time_steps=" << time_steps << std::endl;
			file2.close();
			return false;
		}

		for (int i = 0; i < num_nodes; ++i)
		{
			std::string lineA, lineB, lineC;

			// 必须检查 EOF
			if (!std::getline(file2, lineA) || !std::getline(file2, lineB) || !std::getline(file2, lineC))
			{
				PRINT_ERR << "[Error] Unexpected EOF in ParseTrhist second pass at t_idx="
						  << t_idx << ", node=" << i << std::endl;
				file2.close();
				return false;
			}

			double x_d, y_d, z_d, vx_d, vy_d, vz_d;
			double sx_d, sy_d, sz_d, sxy_d, syz_d, szx_d;
			double efp_d, rho_d, rvol_d;
			int active;

			std::istringstream issA(lineA), issB(lineB), issC(lineC);

			// 必须检查三行数字是否解析成功
			if (!(issA >> x_d >> y_d >> z_d >> vx_d >> vy_d >> vz_d) ||
				!(issB >> sx_d >> sy_d >> sz_d >> sxy_d >> syz_d >> szx_d) ||
				!(issC >> efp_d >> rho_d >> rvol_d >> active))
			{
				PRINT_ERR << "[Error] Failed to parse trhist numeric fields at t_idx="
						  << t_idx << ", node=" << i << std::endl;

				PRINT_ERR << "[Error] lineA = [" << lineA << "]" << std::endl;
				PRINT_ERR << "[Error] lineB = [" << lineB << "]" << std::endl;
				PRINT_ERR << "[Error] lineC = [" << lineC << "]" << std::endl;

				file2.close();
				return false;
			}

			const float x = static_cast<float>(x_d);
			const float y = static_cast<float>(y_d);
			const float z = static_cast<float>(z_d);
			const float vx = static_cast<float>(vx_d);
			const float vy = static_cast<float>(vy_d);
			const float vz = static_cast<float>(vz_d);

			const float sx = static_cast<float>(sx_d);
			const float sy = static_cast<float>(sy_d);
			const float sz = static_cast<float>(sz_d);
			const float sxy = static_cast<float>(sxy_d);
			const float syz = static_cast<float>(syz_d);
			const float szx = static_cast<float>(szx_d);

			const float efp = static_cast<float>(efp_d);
			const float rho = static_cast<float>(rho_d);
			const float rvol = static_cast<float>(rvol_d);

			const float raw_pressure = -(sx + sy + sz) / 3.0f;
			const float overpressure = raw_pressure - P0;

			const int base_idx = i * (time_steps * feature_dim) + t_idx * feature_dim;

			node_features[base_idx + 0] = rho;
			node_features[base_idx + 1] = vx * 0.001f;
			node_features[base_idx + 2] = vy * 0.001f;
			node_features[base_idx + 3] = vz * 0.001f;
			node_features[base_idx + 4] = overpressure;
		}

		t_idx++;
	}

	file2.close();

	// 第二遍结束后，最好再做一次一致性检查
	if (t_idx != time_steps)
	{
		PRINT_ERR << "[Error] Second pass time_steps mismatch. parsed=" << t_idx
				  << ", expected=" << time_steps << std::endl;
		return false;
	}

	PRINT_LOG << "[Info] Parsed trhist file. Nodes: " << num_nodes
			  << ", Time steps: " << time_steps
			  << ", Layout: (N, T, D) = (" << num_nodes << ", " << time_steps << ", " << feature_dim << ")" << std::endl;

	PRINT_LOG << "[Info] node_attr dim = " << attr_dim
			  << " [x,y,z,d,nx,ny,nz,W_cbrt,d_wall,d_edge,d_corner]" << std::endl;

	PRINT_LOG << "[Info] Recovered room box: "
			  << "X[" << room_box.min_x << ", " << room_box.max_x << "], "
			  << "Y[" << room_box.min_y << ", " << room_box.max_y << "], "
			  << "Z[" << room_box.min_z << ", " << room_box.max_z << "]" << std::endl;

	PRINT_LOG << "[Info] Estimated ambient pressure P0 = " << P0 << std::endl;

	return true;
}