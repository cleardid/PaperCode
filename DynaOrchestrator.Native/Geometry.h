//
// Created by tianjia on 2026/3/17.
//

#ifndef PI_GNN_GRAPHENGINE_GEOMETRY_H
#define PI_GNN_GRAPHENGINE_GEOMETRY_H

#include <cmath>
#include <vector>

struct MeshUniformGrid;

// 观测点结构体
struct Point
{
	float x, y, z;
	int id;
};

// 障碍物面片结构体（解析自 STL）
struct Triangle
{
	Point v0, v1, v2;
};

// 向量叉乘
inline Point CrossProduct(const Point& a, const Point& b)
{
	return {
		a.y * b.z - a.z * b.y,
		a.z * b.x - a.x * b.z,
		a.x * b.y - a.y * b.x };
}

// 向量点乘
inline float DotProduct(const Point& a, const Point& b)
{
	return a.x * b.x + a.y * b.y + a.z * b.z;
}

// 两点距离计算
inline float CalculateDistance(const Point& p1, const Point& p2)
{
	return std::sqrt((p1.x - p2.x) * (p1.x - p2.x) +
		(p1.y - p2.y) * (p1.y - p2.y) +
		(p1.z - p2.z) * (p1.z - p2.z));
}

// 1. AABB 包围盒快速剔除声明
bool EdgeTriangleAABBIntersect(const Point& p1, const Point& p2, const Triangle& tri);

// 2. Möller-Trumbore 射线-面片求交算法声明
bool RayIntersectsTriangle(const Point& ray_origin, const Point& ray_vector, const Triangle& tri, float max_dist);

// 3. 全局视线 (LoS) 遮挡检测声明
bool IsLineOfSightClear(
	const Point& p1,
	const Point& p2,
	const std::vector<Triangle>& mesh,
	const MeshUniformGrid& accel);

#endif // PI_GNN_GRAPHENGINE_GEOMETRY_H
