//
// Created by tianjia on 2026/3/17.
//

// 实现 Möller–Trumbore 算法，用于计算节点间连线（射线）是否被 STL 面片遮挡（LoS 判断）

#include "Geometry.h"
#include "MeshAccel.h"
#include <cmath>

const float EPSILON = 1e-6f;

// 1. AABB 包围盒快速剔除 (过滤无效面片求交)
bool EdgeTriangleAABBIntersect(const Point& p1, const Point& p2, const Triangle& tri)
{
	float min_tx = std::min({ tri.v0.x, tri.v1.x, tri.v2.x });
	float max_tx = std::max({ tri.v0.x, tri.v1.x, tri.v2.x });
	float min_ex = std::min(p1.x, p2.x);
	float max_ex = std::max(p1.x, p2.x);
	if (max_ex < min_tx || min_ex > max_tx)
		return false;

	float min_ty = std::min({ tri.v0.y, tri.v1.y, tri.v2.y });
	float max_ty = std::max({ tri.v0.y, tri.v1.y, tri.v2.y });
	float min_ey = std::min(p1.y, p2.y);
	float max_ey = std::max(p1.y, p2.y);
	if (max_ey < min_ty || min_ey > max_ty)
		return false;

	float min_tz = std::min({ tri.v0.z, tri.v1.z, tri.v2.z });
	float max_tz = std::max({ tri.v0.z, tri.v1.z, tri.v2.z });
	float min_ez = std::min(p1.z, p2.z);
	float max_ez = std::max(p1.z, p2.z);
	if (max_ez < min_tz || min_ez > max_tz)
		return false;

	return true;
}

// Möller-Trumbore 射线-三角形求交算法
bool RayIntersectsTriangle(const Point& ray_origin, const Point& ray_vector,
	const Triangle& tri, float max_dist)
{
	float edge1[3] = { tri.v1.x - tri.v0.x, tri.v1.y - tri.v0.y, tri.v1.z - tri.v0.z };
	float edge2[3] = { tri.v2.x - tri.v0.x, tri.v2.y - tri.v0.y, tri.v2.z - tri.v0.z };
	float h[3] = { ray_vector.y * edge2[2] - ray_vector.z * edge2[1],
				  ray_vector.z * edge2[0] - ray_vector.x * edge2[2],
				  ray_vector.x * edge2[1] - ray_vector.y * edge2[0] };
	float a = edge1[0] * h[0] + edge1[1] * h[1] + edge1[2] * h[2];

	// 射线平行于三角形平面
	if (a > -EPSILON && a < EPSILON)
		return false;

	float f = 1.0f / a;
	float s[3] = { ray_origin.x - tri.v0.x, ray_origin.y - tri.v0.y, ray_origin.z - tri.v0.z };
	float u = f * (s[0] * h[0] + s[1] * h[1] + s[2] * h[2]);
	if (u < 0.0f || u > 1.0f)
		return false;

	float q[3] = { s[1] * edge1[2] - s[2] * edge1[1],
				  s[2] * edge1[0] - s[0] * edge1[2],
				  s[0] * edge1[1] - s[1] * edge1[0] };
	float v = f * (ray_vector.x * q[0] + ray_vector.y * q[1] + ray_vector.z * q[2]);
	if (v < 0.0f || u + v > 1.0f)
		return false;

	float t = f * (edge2[0] * q[0] + edge2[1] * q[1] + edge2[2] * q[2]);

	// 存在交点且交点在两测点之间
	if (t > EPSILON && t < max_dist)
		return true;

	return false;
}

// 3. 视线遮挡检测 (AABB + 射线求交)
bool IsLineOfSightClear(
	const Point& p1,
	const Point& p2,
	const std::vector<Triangle>& mesh,
	const MeshUniformGrid& accel)
{
	if (mesh.empty())
		return true;

	Point ray_vector = { p2.x - p1.x, p2.y - p1.y, p2.z - p1.z, -1 };

	// 只查询线段包围盒覆盖到的候选三角面
	std::vector<int> candidate_ids = QueryCandidateTriangles(p1, p2, accel);

	for (int tri_id : candidate_ids)
	{
		const auto& tri = mesh[tri_id];

		if (!EdgeTriangleAABBIntersect(p1, p2, tri))
			continue;

		if (RayIntersectsTriangle(p1, ray_vector, tri, 1.0f))
			return false;
	}

	return true;
}