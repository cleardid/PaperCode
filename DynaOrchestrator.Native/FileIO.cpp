//
// Created by tianjia on 2026/3/17.
// Optimized for Massive I/O & High Performance Parsing (Windows Only)
//

#include "FileIO.h"
#include "Logger.h"

#include <algorithm>
#include <cerrno>
#include <charconv>
#include <cmath>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <string>
#include <system_error>
#include <vector>

#include <omp.h>

#define NOMINMAX
#include <windows.h>

namespace
{
	struct RoomBox { float min_x, max_x, min_y, max_y, min_z, max_z; };
	struct Segment3 { Point a, b; };
	struct AxisPlaneGroup { char axis; float plane_coord; std::vector<Triangle> faces; };

	constexpr float EPS = 1e-12f;

	static Point Add(const Point& a, const Point& b) { return { a.x + b.x, a.y + b.y, a.z + b.z, -1 }; }
	static Point Sub(const Point& a, const Point& b) { return { a.x - b.x, a.y - b.y, a.z - b.z, -1 }; }
	static Point Mul(const Point& a, float s) { return { a.x * s, a.y * s, a.z * s, -1 }; }
	static float Dot(const Point& a, const Point& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
	static float Length(const Point& a) { return std::sqrt(Dot(a, a)); }
	static float Distance(const Point& a, const Point& b) { return Length(Sub(a, b)); }

	static Point Cross(const Point& a, const Point& b)
	{
		return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x, -1 };
	}

	static void AddTriangleToPlaneGroup(std::vector<AxisPlaneGroup>& groups, char axis, float coord, const Triangle& tri, float merge_tol)
	{
		for (auto& g : groups)
		{
			if (std::fabs(g.plane_coord - coord) <= merge_tol)
			{
				g.faces.push_back(tri);
				const int n = static_cast<int>(g.faces.size());
				g.plane_coord = (g.plane_coord * (n - 1) + coord) / static_cast<float>(n);
				return;
			}
		}
		groups.push_back({ axis, coord, { tri } });
	}

	static float ExtractAxisExtremeFromGroup(const AxisPlaneGroup& group, bool take_min)
	{
		float v = take_min ? std::numeric_limits<float>::infinity() : -std::numeric_limits<float>::infinity();

		for (const auto& tri : group.faces)
		{
			auto upd = [&](float x) { v = take_min ? std::min(v, x) : std::max(v, x); };

			if (group.axis == 'X') { upd(tri.v0.x); upd(tri.v1.x); upd(tri.v2.x); }
			else if (group.axis == 'Y') { upd(tri.v0.y); upd(tri.v1.y); upd(tri.v2.y); }
			else { upd(tri.v0.z); upd(tri.v1.z); upd(tri.v2.z); }
		}

		if (!std::isfinite(v))
			throw std::runtime_error("Failed to extract valid boundary value from STL wall group.");

		return v;
	}

	static RoomBox ExtractRoomBoxFromStl(const std::vector<Triangle>& stl_mesh)
	{
		if (stl_mesh.empty())
			throw std::runtime_error("STL mesh is empty. Cannot recover room box.");

		const float normal_tol = 0.90f, plane_merge_tol = 1e-4f;
		std::vector<AxisPlaneGroup> xs, ys, zs;
		xs.reserve(8); ys.reserve(8); zs.reserve(8);

		for (const auto& tri : stl_mesh)
		{
			Point n = Cross(Sub(tri.v1, tri.v0), Sub(tri.v2, tri.v0));
			float len = Length(n);
			if (len < EPS) continue;

			n = Mul(n, 1.0f / len);
			float ax = std::fabs(n.x), ay = std::fabs(n.y), az = std::fabs(n.z);

			if (ax >= ay && ax >= az && ax >= normal_tol)
				AddTriangleToPlaneGroup(xs, 'X', (tri.v0.x + tri.v1.x + tri.v2.x) / 3.0f, tri, plane_merge_tol);
			else if (ay >= ax && ay >= az && ay >= normal_tol)
				AddTriangleToPlaneGroup(ys, 'Y', (tri.v0.y + tri.v1.y + tri.v2.y) / 3.0f, tri, plane_merge_tol);
			else if (az >= ax && az >= ay && az >= normal_tol)
				AddTriangleToPlaneGroup(zs, 'Z', (tri.v0.z + tri.v1.z + tri.v2.z) / 3.0f, tri, plane_merge_tol);
		}

		if (xs.size() < 2 || ys.size() < 2 || zs.size() < 2)
			throw std::runtime_error("Failed to recover 6 main walls from STL.");

		auto cmp = [](const AxisPlaneGroup& a, const AxisPlaneGroup& b) { return a.plane_coord < b.plane_coord; };

		auto xmin = std::min_element(xs.begin(), xs.end(), cmp);
		auto xmax = std::max_element(xs.begin(), xs.end(), cmp);
		auto ymin = std::min_element(ys.begin(), ys.end(), cmp);
		auto ymax = std::max_element(ys.begin(), ys.end(), cmp);
		auto zmin = std::min_element(zs.begin(), zs.end(), cmp);
		auto zmax = std::max_element(zs.begin(), zs.end(), cmp);

		return {
			ExtractAxisExtremeFromGroup(*xmin, true),
			ExtractAxisExtremeFromGroup(*xmax, false),
			ExtractAxisExtremeFromGroup(*ymin, true),
			ExtractAxisExtremeFromGroup(*ymax, false),
			ExtractAxisExtremeFromGroup(*zmin, true),
			ExtractAxisExtremeFromGroup(*zmax, false)
		};
	}

	static std::vector<Point> BuildCorners(const RoomBox& b)
	{
		return {
			{b.min_x, b.min_y, b.min_z, -1}, {b.min_x, b.min_y, b.max_z, -1},
			{b.min_x, b.max_y, b.min_z, -1}, {b.min_x, b.max_y, b.max_z, -1},
			{b.max_x, b.min_y, b.min_z, -1}, {b.max_x, b.min_y, b.max_z, -1},
			{b.max_x, b.max_y, b.min_z, -1}, {b.max_x, b.max_y, b.max_z, -1}
		};
	}

	static std::vector<Segment3> BuildEdges(const RoomBox& b)
	{
		auto c = BuildCorners(b);
		return {
			{c[0], c[2]}, {c[2], c[6]}, {c[6], c[4]}, {c[4], c[0]},
			{c[1], c[3]}, {c[3], c[7]}, {c[7], c[5]}, {c[5], c[1]},
			{c[0], c[1]}, {c[2], c[3]}, {c[6], c[7]}, {c[4], c[5]}
		};
	}

	static float ComputeWallDistance(const Point& p, const RoomBox& b)
	{
		return std::min({
			std::fabs(p.x - b.min_x), std::fabs(b.max_x - p.x),
			std::fabs(p.y - b.min_y), std::fabs(b.max_y - p.y),
			std::fabs(p.z - b.min_z), std::fabs(b.max_z - p.z)
			});
	}

	static float ComputeMinCornerDistance(const Point& p, const std::vector<Point>& corners)
	{
		float d = std::numeric_limits<float>::max();
		for (const auto& c : corners) d = std::min(d, Distance(p, c));
		return d;
	}

	static float ComputePointSegmentDistance(const Point& p, const Segment3& s)
	{
		Point ab = Sub(s.b, s.a), ap = Sub(p, s.a);
		float ab2 = Dot(ab, ab);
		if (ab2 < EPS) return Distance(p, s.a);

		float t = std::max(0.0f, std::min(1.0f, Dot(ap, ab) / ab2));
		return Distance(p, Add(s.a, Mul(ab, t)));
	}

	static float ComputeMinEdgeDistance(const Point& p, const std::vector<Segment3>& edges)
	{
		float d = std::numeric_limits<float>::max();
		for (const auto& e : edges) d = std::min(d, ComputePointSegmentDistance(p, e));
		return d;
	}

	class MmapScanner
	{
		const char* data_ = nullptr;
		const char* cur_ = nullptr;
		const char* end_ = nullptr;

		HANDLE file_ = INVALID_HANDLE_VALUE;
		HANDLE map_ = NULL;

	public:
		explicit MmapScanner(const char* filepath)
		{
			file_ = CreateFileA(filepath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
			if (file_ == INVALID_HANDLE_VALUE) return;

			LARGE_INTEGER size{};
			if (!GetFileSizeEx(file_, &size) || size.QuadPart <= 0) return;

			map_ = CreateFileMappingA(file_, NULL, PAGE_READONLY, 0, 0, NULL);
			if (!map_) return;

			data_ = static_cast<const char*>(MapViewOfFile(map_, FILE_MAP_READ, 0, 0, 0));
			if (data_)
			{
				cur_ = data_;
				end_ = data_ + static_cast<size_t>(size.QuadPart);
			}
		}

		~MmapScanner()
		{
			if (data_) UnmapViewOfFile(data_);
			if (map_) CloseHandle(map_);
			if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
		}

		bool is_open() const { return data_ != nullptr; }

		bool skip_line()
		{
			while (cur_ < end_ && *cur_ != '\n' && *cur_ != '\r') ++cur_;
			if (cur_ < end_ && *cur_ == '\r') ++cur_;
			if (cur_ < end_ && *cur_ == '\n') ++cur_;
			return true;
		}

		bool read_line(const char*& lb, const char*& le)
		{
			if (cur_ >= end_) return false;

			lb = cur_;
			while (cur_ < end_ && *cur_ != '\n' && *cur_ != '\r') ++cur_;
			le = cur_;

			if (cur_ < end_ && *cur_ == '\r') ++cur_;
			if (cur_ < end_ && *cur_ == '\n') ++cur_;

			return true;
		}

		bool read_nonempty_line(const char*& lb, const char*& le)
		{
			while (read_line(lb, le))
			{
				const char* p = lb;
				while (p < le && std::isspace(static_cast<unsigned char>(*p))) ++p;
				if (p < le) return true;
			}
			return false;
		}
	};

	static std::string MakeLineSnippet(const char* lb, const char* le)
	{
		if (!lb || !le || le < lb) return "";
		size_t n = std::min<size_t>(static_cast<size_t>(le - lb), 256);
		return std::string(lb, lb + n);
	}

	static inline bool NextToken(const char*& p, const char* end, const char*& tb, const char*& te)
	{
		while (p < end && std::isspace(static_cast<unsigned char>(*p))) ++p;
		if (p >= end) return false;

		tb = p;
		while (p < end && !std::isspace(static_cast<unsigned char>(*p))) ++p;
		te = p;

		return tb < te;
	}

	static inline bool SkipToken(const char*& p, const char* end)
	{
		const char* tb = nullptr;
		const char* te = nullptr;
		return NextToken(p, end, tb, te);
	}

	static inline bool SkipTokens(const char*& p, const char* end, int n)
	{
		for (int i = 0; i < n; ++i)
			if (!SkipToken(p, end)) return false;
		return true;
	}

	static inline bool AssignDoubleToFloat(double v, float& out)
	{
		if (!std::isfinite(v)) return false;

		float f = static_cast<float>(v);
		if (!std::isfinite(f)) return false;

		out = f;
		return true;
	}

	// [新增/优化] 无堆分配数值解析。兼容 E/e、D/d、省略 E 的 Fortran 指数格式，以及极小值下溢。
	static inline bool ParseFloatTokenFast(const char* tb, const char* te, float& out)
	{
		if (!tb || !te || te <= tb) return false;

		bool has_exp = false, need_norm = false;

		for (const char* p = tb; p < te; ++p)
		{
			char c = *p;
			if (c == 'E' || c == 'e') has_exp = true;
			else if (c == 'D' || c == 'd') { has_exp = true; need_norm = true; }
		}

		if (!has_exp)
		{
			for (const char* p = tb + 1; p < te; ++p)
			{
				if (*p == '+' || *p == '-')
				{
					need_norm = true;
					break;
				}
			}
		}

		if (!need_norm)
		{
			const char* begin = (tb < te && *tb == '+') ? tb + 1 : tb;

			double v = 0.0;
			auto [ptr, ec] = std::from_chars(begin, te, v);

			if (ec == std::errc() && ptr == te)
				return AssignDoubleToFloat(v, out);
		}

		size_t len = static_cast<size_t>(te - tb);
		if (len + 2 >= 96) return false;

		char buf[96];
		size_t n = 0;
		bool normalized_has_exp = false;

		for (const char* p = tb; p < te; ++p)
		{
			char c = *p;

			if (c == 'D' || c == 'd') { c = 'E'; normalized_has_exp = true; }
			else if (c == 'E' || c == 'e') { c = 'E'; normalized_has_exp = true; }

			buf[n++] = c;
		}

		if (!normalized_has_exp)
		{
			for (int i = static_cast<int>(n) - 1; i >= 1; --i)
			{
				if (buf[i] != '+' && buf[i] != '-') continue;

				bool ok = true, has_digit = false;

				for (size_t j = static_cast<size_t>(i + 1); j < n; ++j)
				{
					if (std::isdigit(static_cast<unsigned char>(buf[j]))) has_digit = true;
					else { ok = false; break; }
				}

				if (ok && has_digit)
				{
					if (n + 1 >= sizeof(buf)) return false;
					std::memmove(buf + i + 1, buf + i, n - static_cast<size_t>(i));
					buf[i] = 'E';
					++n;
				}
				break;
			}
		}

		buf[n] = '\0';

		char* endp = nullptr;
		errno = 0;

		double v = std::strtod(buf, &endp);

		if (endp != buf + n) return false;

		if (errno == ERANGE && v == 0.0)
		{
			out = 0.0f;
			return true;
		}

		if (errno == ERANGE && !std::isfinite(v)) return false;

		return AssignDoubleToFloat(v, out);
	}

	static inline bool ParseNextFloatFast(const char*& p, const char* end, float& out)
	{
		const char* tb = nullptr;
		const char* te = nullptr;
		return NextToken(p, end, tb, te) && ParseFloatTokenFast(tb, te, out);
	}

	static bool ParseLineFloats(const char* lb, const char* le, float* out, int expected)
	{
		const char* p = lb;
		for (int i = 0; i < expected; ++i)
			if (!ParseNextFloatFast(p, le, out[i])) return false;

		const char* tb = nullptr;
		const char* te = nullptr;
		return !NextToken(p, le, tb, te);
	}

	// [新增/优化] 只解析后续真正使用的字段。
	static inline bool ParseTracerRecordFast(
		const char* l1b, const char* l1e,
		const char* l2b, const char* l2e,
		const char* l3b, const char* l3e,
		bool need_xyz,
		float& x, float& y, float& z,
		float& vx, float& vy, float& vz,
		float& sx, float& sy, float& sz,
		float& rho)
	{
		const char* p = l1b;

		if (need_xyz)
		{
			if (!ParseNextFloatFast(p, l1e, x)) return false;
			if (!ParseNextFloatFast(p, l1e, y)) return false;
			if (!ParseNextFloatFast(p, l1e, z)) return false;
		}
		else if (!SkipTokens(p, l1e, 3)) return false;

		if (!ParseNextFloatFast(p, l1e, vx)) return false;
		if (!ParseNextFloatFast(p, l1e, vy)) return false;
		if (!ParseNextFloatFast(p, l1e, vz)) return false;

		p = l2b;
		if (!ParseNextFloatFast(p, l2e, sx)) return false;
		if (!ParseNextFloatFast(p, l2e, sy)) return false;
		if (!ParseNextFloatFast(p, l2e, sz)) return false;
		if (!SkipTokens(p, l2e, 3)) return false; // sxy, syz, szx

		p = l3b;
		if (!SkipToken(p, l3e)) return false;              // efp
		if (!ParseNextFloatFast(p, l3e, rho)) return false;
		if (!SkipTokens(p, l3e, 2)) return false;           // rvol, active

		return true;
	}
}

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

	uint32_t ntri = 0;
	file.read(reinterpret_cast<char*>(&ntri), sizeof(uint32_t));

	if (ntri == 0) return mesh;

	mesh.reserve(ntri);

	float normal[3], v0[3], v1[3], v2[3];
	uint16_t attr = 0;

	for (uint32_t i = 0; i < ntri; ++i)
	{
		file.read(reinterpret_cast<char*>(normal), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v0), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v1), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(v2), 3 * sizeof(float));
		file.read(reinterpret_cast<char*>(&attr), sizeof(uint16_t));

		mesh.push_back({
			{v0[0] * 0.001f, v0[1] * 0.001f, v0[2] * 0.001f, -1},
			{v1[0] * 0.001f, v1[1] * 0.001f, v1[2] * 0.001f, -1},
			{v2[0] * 0.001f, v2[1] * 0.001f, v2[2] * 0.001f, -1}
			});
	}

	return mesh;
}

bool ParseTrhist(const char* filepath, const std::vector<Triangle>& stl_mesh,
	std::vector<Point>& nodes, std::vector<float>& node_features, std::vector<float>& node_attrs,
	int& num_nodes, int& time_steps, int& feature_dim, int& attr_dim,
	float Xc, float Yc, float Zc, float W)
{
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

	// trhist header:
	// 1: Tracer particle file
	// 2: <num_nodes> <num_vars>
	// 3: x y z vx vy vz
	// 4: sx sy sz sxy syz szx
	// 5: efp rho rvol active
	if (!scanner.skip_line())
	{
		PRINT_ERR << "[Error] Failed to skip trhist title line." << std::endl;
		return false;
	}

	const char* lb = nullptr;
	const char* le = nullptr;
	float header_vals[2] = { 0.0f, 0.0f };

	if (!scanner.read_nonempty_line(lb, le) || !ParseLineFloats(lb, le, header_vals, 2))
	{
		PRINT_ERR << "[Error] Failed to parse trhist header line: \"" << MakeLineSnippet(lb, le) << "\"" << std::endl;
		return false;
	}

	num_nodes = static_cast<int>(header_vals[0] + 0.5f);
	const int file_var_count = static_cast<int>(header_vals[1] + 0.5f);

	if (num_nodes <= 0 || file_var_count != 16)
	{
		PRINT_ERR << "[Error] Unsupported trhist header. num_nodes=" << num_nodes
			<< ", num_vars=" << file_var_count << ". Expected num_vars=16." << std::endl;
		return false;
	}

	for (int i = 0; i < 3; ++i)
	{
		if (!scanner.skip_line())
		{
			PRINT_ERR << "[Error] Unexpected EOF while skipping trhist variable-name lines." << std::endl;
			return false;
		}
	}

	feature_dim = 5; // rho, vx, vy, vz, pressure
	attr_dim = 11;   // x, y, z, d, nx, ny, nz, W^(1/3), wall_dist, edge_dist, corner_dist

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

	nodes.clear();
	nodes.reserve(num_nodes);

	node_attrs.clear();
	node_attrs.reserve(static_cast<size_t>(num_nodes) * attr_dim);

	node_features.clear();

	const float W_cbrt = std::cbrt(W);
	const float Xc_m = Xc * 0.001f;
	const float Yc_m = Yc * 0.001f;
	const float Zc_m = Zc * 0.001f;

	time_steps = 0;

	std::vector<float> time_major_features;
	time_major_features.reserve(static_cast<size_t>(num_nodes) * 100 * feature_dim);

	while (true)
	{
		const char* tlb = nullptr;
		const char* tle = nullptr;

		if (!scanner.read_nonempty_line(tlb, tle))
			break;

		float tval[1] = { 0.0f };
		if (!ParseLineFloats(tlb, tle, tval, 1))
		{
			PRINT_ERR << "[Error] Failed to parse trhist time line at step " << time_steps
				<< ": \"" << MakeLineSnippet(tlb, tle) << "\"" << std::endl;
			return false;
		}

		const size_t step_base = time_major_features.size();

		try
		{
			time_major_features.resize(step_base + static_cast<size_t>(num_nodes) * feature_dim);
		}
		catch (const std::bad_alloc&)
		{
			PRINT_ERR << "[Error] Out of memory while allocating time-major feature block." << std::endl;
			return false;
		}

		float* step_features = time_major_features.data() + step_base;
		const bool need_xyz = (time_steps == 0);

		for (int i = 0; i < num_nodes; ++i)
		{
			const char* l1b = nullptr, * l1e = nullptr;
			const char* l2b = nullptr, * l2e = nullptr;
			const char* l3b = nullptr, * l3e = nullptr;

			// tracer 记录固定三行。这里用 read_line，比 read_nonempty_line 少判断，速度更高。
			if (!scanner.read_line(l1b, l1e) || !scanner.read_line(l2b, l2e) || !scanner.read_line(l3b, l3e))
			{
				PRINT_ERR << "[Error] Unexpected EOF at step " << time_steps
					<< ", node " << i << " while reading tracer record." << std::endl;
				return false;
			}

			float x = 0.0f, y = 0.0f, z = 0.0f;
			float vx = 0.0f, vy = 0.0f, vz = 0.0f;
			float sx = 0.0f, sy = 0.0f, sz = 0.0f;
			float rho = 0.0f;

			if (!ParseTracerRecordFast(l1b, l1e, l2b, l2e, l3b, l3e,
				need_xyz, x, y, z, vx, vy, vz, sx, sy, sz, rho))
			{
				PRINT_ERR << "[Error] Parsing aborted at step " << time_steps
					<< ", node " << i << " while parsing tracer record." << std::endl;
				PRINT_ERR << "[Error] Line 1: \"" << MakeLineSnippet(l1b, l1e) << "\"" << std::endl;
				PRINT_ERR << "[Error] Line 2: \"" << MakeLineSnippet(l2b, l2e) << "\"" << std::endl;
				PRINT_ERR << "[Error] Line 3: \"" << MakeLineSnippet(l3b, l3e) << "\"" << std::endl;
				return false;
			}

			const float pressure = -(sx + sy + sz) / 3.0f;

			if (time_steps == 0)
			{
				Point p{ x * 0.001f, y * 0.001f, z * 0.001f, i };
				nodes.push_back(p);

				const float dx = p.x - Xc_m;
				const float dy = p.y - Yc_m;
				const float dz = p.z - Zc_m;
				const float d = std::sqrt(dx * dx + dy * dy + dz * dz);
				const float inv_d = d > 1e-6f ? 1.0f / d : 0.0f;

				node_attrs.push_back(p.x);
				node_attrs.push_back(p.y);
				node_attrs.push_back(p.z);
				node_attrs.push_back(d);
				node_attrs.push_back(d > 1e-6f ? dx * inv_d : 0.0f);
				node_attrs.push_back(d > 1e-6f ? dy * inv_d : 0.0f);
				node_attrs.push_back(d > 1e-6f ? dz * inv_d : 1.0f);
				node_attrs.push_back(W_cbrt);
				node_attrs.push_back(ComputeWallDistance(p, room_box));
				node_attrs.push_back(ComputeMinEdgeDistance(p, edges));
				node_attrs.push_back(ComputeMinCornerDistance(p, corners));
			}

			float* f = step_features + static_cast<size_t>(i) * feature_dim;
			f[0] = rho;
			f[1] = vx * 0.001f;
			f[2] = vy * 0.001f;
			f[3] = vz * 0.001f;
			f[4] = pressure;
		}

		++time_steps;
	}

	if (time_steps <= 0)
	{
		PRINT_ERR << "[Error] No valid time steps found in trhist file." << std::endl;
		return false;
	}

	const size_t expected = static_cast<size_t>(num_nodes) * static_cast<size_t>(time_steps) * feature_dim;

	if (time_major_features.size() != expected)
	{
		PRINT_ERR << "[Error] Parsed feature count mismatch. got=" << time_major_features.size()
			<< ", expected=" << expected << std::endl;
		return false;
	}

	try
	{
		node_features.resize(expected);
	}
	catch (const std::bad_alloc&)
	{
		PRINT_ERR << "[Error] Out of memory while allocating node_features." << std::endl;
		return false;
	}

#pragma omp parallel for
	for (int i = 0; i < num_nodes; ++i)
	{
		for (int t = 0; t < time_steps; ++t)
		{
			const size_t src = (static_cast<size_t>(t) * num_nodes + i) * feature_dim;
			const size_t dst = (static_cast<size_t>(i) * time_steps + t) * feature_dim;

			node_features[dst + 0] = time_major_features[src + 0];
			node_features[dst + 1] = time_major_features[src + 1];
			node_features[dst + 2] = time_major_features[src + 2];
			node_features[dst + 3] = time_major_features[src + 3];
			node_features[dst + 4] = time_major_features[src + 4];
		}
	}

	PRINT_LOG << "[Info] Parsed trhist via optimized Windows mmap parser. Nodes: "
		<< num_nodes << ", Time steps: " << time_steps << std::endl;

	return true;
}