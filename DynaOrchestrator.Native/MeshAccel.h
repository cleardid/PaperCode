#ifndef PI_GNN_GRAPHENGINE_MESHACCEL_H
#define PI_GNN_GRAPHENGINE_MESHACCEL_H

#include <vector>
#include <unordered_set>

struct Triangle;
struct Point;

struct MeshUniformGrid
{
	float min_x, min_y, min_z;
	float max_x, max_y, max_z;
	float cell_size;
	int nx, ny, nz;

	// 每个网格单元存储可能落入该单元的 triangle index
	std::vector<std::vector<int>> cells;
};

// 基于三角面 AABB 构建 Uniform Grid
MeshUniformGrid BuildMeshUniformGrid(const std::vector<Triangle>& mesh, float cell_size);

// 查询与线段包围盒重叠的候选三角面 index
std::vector<int> QueryCandidateTriangles(
	const Point& p1,
	const Point& p2,
	const MeshUniformGrid& grid);

#endif