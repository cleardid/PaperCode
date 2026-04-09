//
// Created by tianjia on 2026/3/17.
// Optimized for Massive I/O & High Performance Parsing
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

namespace
{
	struct RoomBox { float min_x, max_x; float min_y, max_y; float min_z, max_z; };
	struct Segment3 { Point a; Point b; };
	struct AxisPlaneGroup { char axis; float plane_coord; std::vector<Triangle> faces; };

	constexpr float EPS = 1e-12f;

	static Point Add(const Point& a, const Point& b) { return { a.x + b.x, a.y + b.y, a.z + b.z, -1 }; }
	static Point Sub(const Point& a, const Point& b) { return { a.x - b.x, a.y - b.y, a.z - b.z, -1 }; }
	static Point Mul(const Point& a, float s) { return { a.x * s, a.y * s, a.z * s, -1 }; }
	static float Dot(const Point& a, const Point& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
	static Point Cross(const Point& a, const Point& b) {
		return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x, -1 };
	}
	static float Length(const Point& a) { return std::sqrt(Dot(a, a)); }
	static float Distance(const Point& a, const Point& b) { return Length(Sub(a, b)); }

	static void AddTriangleToPlaneGroup(std::vector<AxisPlaneGroup>& groups, char axis, float coord, const Triangle& tri, float merge_tol) {
		for (auto& g : groups) {
			if (std::fabs(g.plane_coord - coord) <= merge_tol) {
				g.faces.push_back(tri);
				const int count = static_cast<int>(g.faces.size());
				g.plane_coord = (g.plane_coord * (count - 1) + coord) / static_cast<float>(count);
				return;
			}
		}
		AxisPlaneGroup group;
		group.axis = axis; group.plane_coord = coord; group.faces.push_back(tri);
		groups.push_back(std::move(group));
	}

	static float ExtractAxisExtremeFromGroup(const AxisPlaneGroup& group, bool take_min) {
		float value = take_min ? std::numeric_limits<float>::infinity() : -std::numeric_limits<float>::infinity();
		auto update_val = [&](float v) { value = take_min ? std::min(value, v) : std::max(value, v); };
		for (const auto& tri : group.faces) {
			if (group.axis == 'X') { update_val(tri.v0.x); update_val(tri.v1.x); update_val(tri.v2.x); }
			else if (group.axis == 'Y') { update_val(tri.v0.y); update_val(tri.v1.y); update_val(tri.v2.y); }
			else { update_val(tri.v0.z); update_val(tri.v1.z); update_val(tri.v2.z); }
		}
		if (!std::isfinite(value)) throw std::runtime_error("Failed to extract valid boundary value from STL wall group.");
		return value;
	}

	static RoomBox ExtractRoomBoxFromStl(const std::vector<Triangle>& stl_mesh) {
		if (stl_mesh.empty()) throw std::runtime_error("STL mesh is empty. Cannot recover room box.");
		const float normal_tol = 0.90f, plane_merge_tol = 1e-4f;
		std::vector<AxisPlaneGroup> x_groups, y_groups, z_groups;
		x_groups.reserve(8); y_groups.reserve(8); z_groups.reserve(8);

		for (const auto& tri : stl_mesh) {
			Point e1 = Sub(tri.v1, tri.v0), e2 = Sub(tri.v2, tri.v0);
			Point n = Cross(e1, e2);
			float len = Length(n);
			if (len < EPS) continue;
			n = Mul(n, 1.0f / len);
			float ax = std::fabs(n.x), ay = std::fabs(n.y), az = std::fabs(n.z);

			if (ax >= ay && ax >= az && ax >= normal_tol) {
				AddTriangleToPlaneGroup(x_groups, 'X', (tri.v0.x + tri.v1.x + tri.v2.x) / 3.0f, tri, plane_merge_tol);
			}
			else if (ay >= ax && ay >= az && ay >= normal_tol) {
				AddTriangleToPlaneGroup(y_groups, 'Y', (tri.v0.y + tri.v1.y + tri.v2.y) / 3.0f, tri, plane_merge_tol);
			}
			else if (az >= ax && az >= ay && az >= normal_tol) {
				AddTriangleToPlaneGroup(z_groups, 'Z', (tri.v0.z + tri.v1.z + tri.v2.z) / 3.0f, tri, plane_merge_tol);
			}
		}

		if (x_groups.size() < 2 || y_groups.size() < 2 || z_groups.size() < 2) throw std::runtime_error("Failed to recover 6 main walls from STL.");

		auto cmp = [](const AxisPlaneGroup& a, const AxisPlaneGroup& b) { return a.plane_coord < b.plane_coord; };
		auto x_min_it = std::min_element(x_groups.begin(), x_groups.end(), cmp);
		auto x_max_it = std::max_element(x_groups.begin(), x_groups.end(), cmp);
		auto y_min_it = std::min_element(y_groups.begin(), y_groups.end(), cmp);
		auto y_max_it = std::max_element(y_groups.begin(), y_groups.end(), cmp);
		auto z_min_it = std::min_element(z_groups.begin(), z_groups.end(), cmp);
		auto z_max_it = std::max_element(z_groups.begin(), z_groups.end(), cmp);

		RoomBox box{
			ExtractAxisExtremeFromGroup(*x_min_it, true), ExtractAxisExtremeFromGroup(*x_max_it, false),
			ExtractAxisExtremeFromGroup(*y_min_it, true), ExtractAxisExtremeFromGroup(*y_max_it, false),
			ExtractAxisExtremeFromGroup(*z_min_it, true), ExtractAxisExtremeFromGroup(*z_max_it, false)
		};
		return box;
	}

	static std::vector<Point> BuildCorners(const RoomBox& box) {
		return {
			{box.min_x, box.min_y, box.min_z, -1}, {box.min_x, box.min_y, box.max_z, -1},
			{box.min_x, box.max_y, box.min_z, -1}, {box.min_x, box.max_y, box.max_z, -1},
			{box.max_x, box.min_y, box.min_z, -1}, {box.max_x, box.min_y, box.max_z, -1},
			{box.max_x, box.max_y, box.min_z, -1}, {box.max_x, box.max_y, box.max_z, -1}
		};
	}

	static std::vector<Segment3> BuildEdges(const RoomBox& box) {
		auto c = BuildCorners(box);
		return {
			{c[0], c[2]}, {c[2], c[6]}, {c[6], c[4]}, {c[4], c[0]},
			{c[1], c[3]}, {c[3], c[7]}, {c[7], c[5]}, {c[5], c[1]},
			{c[0], c[1]}, {c[2], c[3]}, {c[6], c[7]}, {c[4], c[5]}
		};
	}

	static float ComputeWallDistance(const Point& p, const RoomBox& box) {
		return std::min({
			std::fabs(p.x - box.min_x), std::fabs(box.max_x - p.x),
			std::fabs(p.y - box.min_y), std::fabs(box.max_y - p.y),
			std::fabs(p.z - box.min_z), std::fabs(box.max_z - p.z)
			});
	}

	static float ComputeMinCornerDistance(const Point& p, const std::vector<Point>& corners) {
		float min_dist = std::numeric_limits<float>::max();
		for (const auto& c : corners) min_dist = std::min(min_dist, Distance(p, c));
		return min_dist;
	}

	static float ComputePointSegmentDistance(const Point& p, const Segment3& seg) {
		Point ab = Sub(seg.b, seg.a), ap = Sub(p, seg.a);
		float ab2 = Dot(ab, ab);
		if (ab2 < EPS) return Distance(p, seg.a);
		float t = std::max(0.0f, std::min(1.0f, Dot(ap, ab) / ab2));
		return Distance(p, Add(seg.a, Mul(ab, t)));
	}

	static float ComputeMinEdgeDistance(const Point& p, const std::vector<Segment3>& edges) {
		float min_dist = std::numeric_limits<float>::max();
		for (const auto& e : edges) min_dist = std::min(min_dist, ComputePointSegmentDistance(p, e));
		return min_dist;
	}

	// 极速无锁无 Locale 浮点状态机解析算法
	static bool fast_atof(char*& p, char* end, float& result) {
		while (p < end && std::isspace(static_cast<unsigned char>(*p))) p++;
		if (p >= end) return false;

		bool negative = false;
		if (*p == '-') { negative = true; p++; }
		else if (*p == '+') { p++; }

		double val = 0.0;
		bool has_digits = false;
		while (p < end && *p >= '0' && *p <= '9') {
			val = val * 10.0 + (*p - '0');
			p++; has_digits = true;
		}

		if (p < end && *p == '.') {
			p++;
			double frac = 1.0;
			while (p < end && *p >= '0' && *p <= '9') {
				frac *= 0.1;
				val += (*p - '0') * frac;
				p++; has_digits = true;
			}
		}

		if (!has_digits) return false;

		if (p < end && (*p == 'e' || *p == 'E')) {
			p++;
			bool exp_negative = false;
			if (p < end && *p == '-') { exp_negative = true; p++; }
			else if (p < end && *p == '+') { p++; }
			int exp_val = 0;
			while (p < end && *p >= '0' && *p <= '9') {
				exp_val = exp_val * 10 + (*p - '0');
				p++;
			}
			val *= std::pow(10.0, exp_negative ? -exp_val : exp_val);
		}

		result = static_cast<float>(negative ? -val : val);
		return true;
	}

	// 16MB 分块高速文件扫描器，专攻海量文本
	class FastScanner {
		FILE* fp = nullptr;
		std::vector<char> buffer;
		char* cur = nullptr;
		char* end = nullptr;
		bool is_eof = false;

		void fill_buffer() {
			if (is_eof) return;
			size_t remaining = end - cur;
			if (remaining > 0) std::memmove(buffer.data(), cur, remaining);
			size_t bytes_to_read = buffer.size() - remaining - 1;
			size_t bytes_read = std::fread(buffer.data() + remaining, 1, bytes_to_read, fp);
			cur = buffer.data();
			end = cur + remaining + bytes_read;
			*end = '\0';
			if (bytes_read < bytes_to_read) is_eof = true;
		}

	public:
		FastScanner(const char* filepath) {
			fp = std::fopen(filepath, "rb");
			if (fp) {
				buffer.resize(16 * 1024 * 1024); // 16MB L3 Cache Friendly Buffer
				cur = end = buffer.data();
				fill_buffer();
			}
		}
		~FastScanner() { if (fp) std::fclose(fp); }
		bool is_open() const { return fp != nullptr; }

		bool next_float(float& val) {
			while (cur < end && std::isspace(static_cast<unsigned char>(*cur))) cur++;
			if (end - cur < 64 && !is_eof) {
				fill_buffer();
				while (cur < end && std::isspace(static_cast<unsigned char>(*cur))) cur++;
			}
			if (cur >= end) return false;
			return fast_atof(cur, end, val);
		}

		bool skip_line() {
			while (cur < end && *cur != '\n') {
				cur++;
				if (end - cur < 64 && !is_eof) fill_buffer();
			}
			if (cur < end && *cur == '\n') {
				cur++;
				return true;
			}
			return false;
		}
	};
}

std::vector<Triangle> ParseSTL(const char* filepath) {
	std::vector<Triangle> mesh;
	std::ifstream file(filepath, std::ios::binary);
	if (!file.is_open()) { PRINT_ERR << "[Error] Failed to open STL file: " << filepath << std::endl; return mesh; }

	char header[80];
	file.read(header, 80);
	uint32_t num_triangles = 0;
	file.read(reinterpret_cast<char*>(&num_triangles), sizeof(uint32_t));

	if (num_triangles == 0) return mesh;

	mesh.reserve(num_triangles);
	float normal[3], v0[3], v1[3], v2[3];
	uint16_t attribute_byte_count = 0;

	for (uint32_t i = 0; i < num_triangles; ++i) {
		file.read(reinterpret_cast<char*>(normal), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v0), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v1), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v2), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(&attribute_byte_count), sizeof(uint16_t));
		mesh.push_back({
			{v0[0] * 0.001f, v0[1] * 0.001f, v0[2] * 0.001f, -1},
			{v1[0] * 0.001f, v1[1] * 0.001f, v1[2] * 0.001f, -1},
			{v2[0] * 0.001f, v2[1] * 0.001f, v2[2] * 0.001f, -1}
			});
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
	FastScanner scanner(filepath);
	if (!scanner.is_open()) {
		PRINT_ERR << "[Error] Failed to open trhist file: " << filepath << std::endl;
		return false;
	}
	if (stl_mesh.empty()) {
		PRINT_ERR << "[Error] STL mesh is empty." << std::endl;
		return false;
	}

	// 高速跳过头部数据
	scanner.skip_line(); // Skip Line 1
	float f_nn, f_nv;
	if (!scanner.next_float(f_nn) || !scanner.next_float(f_nv)) return false;
	num_nodes = static_cast<int>(f_nn);

	scanner.skip_line(); // Skip remainder of Line 2
	scanner.skip_line(); // Skip Line 3
	scanner.skip_line(); // Skip Line 4
	scanner.skip_line(); // Skip Line 5

	feature_dim = 5;
	attr_dim = 11;

	RoomBox room_box;
	std::vector<Point> corners;
	std::vector<Segment3> edges;
	try {
		room_box = ExtractRoomBoxFromStl(stl_mesh);
		corners = BuildCorners(room_box);
		edges = BuildEdges(room_box);
	}
	catch (const std::exception& ex) {
		PRINT_ERR << "[Error] Failed to build room geometry prior: " << ex.what() << std::endl;
		return false;
	}

	nodes.clear(); nodes.reserve(num_nodes);
	node_attrs.clear(); node_attrs.reserve(static_cast<size_t>(num_nodes) * attr_dim);

	const float W_cbrt = std::cbrt(W);
	const float Xc_m = Xc * 0.001f, Yc_m = Yc * 0.001f, Zc_m = Zc * 0.001f;

	time_steps = 0;
	double p0_sum = 0.0;
	int p0_count = 0;
	float P0 = 0.0f;

	// 为单次遍历准备 Time-Major 存储 (T, N, D)
	std::vector<float> time_major_features;
	time_major_features.reserve(static_cast<size_t>(num_nodes) * 100 * feature_dim);

	// Phase 1: 边读边析 (Stream parsing into time-major layout)
	while (true) {
		float time_val;
		if (!scanner.next_float(time_val)) break; // End of file or valid data

		for (int i = 0; i < num_nodes; ++i) {
			float x, y, z, vx, vy, vz, sx, sy, sz, sxy, syz, szx, efp, rho, rvol, active;

			if (!scanner.next_float(x) || !scanner.next_float(y) || !scanner.next_float(z) ||
				!scanner.next_float(vx) || !scanner.next_float(vy) || !scanner.next_float(vz) ||
				!scanner.next_float(sx) || !scanner.next_float(sy) || !scanner.next_float(sz) ||
				!scanner.next_float(sxy) || !scanner.next_float(syz) || !scanner.next_float(szx) ||
				!scanner.next_float(efp) || !scanner.next_float(rho) || !scanner.next_float(rvol) ||
				!scanner.next_float(active)) {
				PRINT_ERR << "[Error] Parsing aborted at step " << time_steps << ", node " << i << std::endl;
				return false;
			}

			float raw_pressure = -(sx + sy + sz) / 3.0f;

			// 初次时间步生成静态拓扑与计算初始场压 P0
			if (time_steps == 0) {
				Point p{ x * 0.001f, y * 0.001f, z * 0.001f, i };
				nodes.push_back(p);

				float dx = p.x - Xc_m, dy = p.y - Yc_m, dz = p.z - Zc_m;
				float d = std::sqrt(dx * dx + dy * dy + dz * dz);

				node_attrs.push_back(p.x);
				node_attrs.push_back(p.y);
				node_attrs.push_back(p.z);
				node_attrs.push_back(d);
				node_attrs.push_back(d > 1e-6f ? dx / d : 0.0f);
				node_attrs.push_back(d > 1e-6f ? dy / d : 0.0f);
				node_attrs.push_back(d > 1e-6f ? dz / d : 1.0f);
				node_attrs.push_back(W_cbrt);
				node_attrs.push_back(ComputeWallDistance(p, room_box));
				node_attrs.push_back(ComputeMinEdgeDistance(p, edges));
				node_attrs.push_back(ComputeMinCornerDistance(p, corners));

				if (std::isfinite(raw_pressure) && std::isfinite(rho) && std::isfinite(rvol) && rho > 0.0f && rvol > 0.0f) {
					p0_sum += raw_pressure;
					p0_count++;
				}
			}

			time_major_features.push_back(rho);
			time_major_features.push_back(vx * 0.001f);
			time_major_features.push_back(vy * 0.001f);
			time_major_features.push_back(vz * 0.001f);
			time_major_features.push_back(raw_pressure); // 暂存未修正的原始压力
		}

		if (time_steps == 0) {
			if (p0_count <= 0) {
				PRINT_ERR << "[Error] Failed to estimate ambient pressure P0." << std::endl;
				return false;
			}
			P0 = static_cast<float>(p0_sum / p0_count);
		}
		time_steps++;
	}

	if (time_steps <= 0) return false;

	// Phase 2: OpenMP 并行矩阵转置 (N, T, D) 并减去 P0
	node_features.resize(static_cast<size_t>(num_nodes) * time_steps * feature_dim, 0.0f);

#pragma omp parallel for
	for (int i = 0; i < num_nodes; ++i) {
		for (int t = 0; t < time_steps; ++t) {
			size_t src_base = (static_cast<size_t>(t) * num_nodes + i) * feature_dim;
			size_t dst_base = (static_cast<size_t>(i) * time_steps + t) * feature_dim;

			node_features[dst_base + 0] = time_major_features[src_base + 0];
			node_features[dst_base + 1] = time_major_features[src_base + 1];
			node_features[dst_base + 2] = time_major_features[src_base + 2];
			node_features[dst_base + 3] = time_major_features[src_base + 3];
			// 运用延迟减法技巧，一次性获取工程超压
			node_features[dst_base + 4] = time_major_features[src_base + 4] - P0;
		}
	}

	PRINT_LOG << "[Info] Single-Pass parsed trhist. Nodes: " << num_nodes << ", Time steps: " << time_steps << std::endl;
	PRINT_LOG << "[Info] Estimated ambient pressure P0 = " << P0 << std::endl;

	return true;
}