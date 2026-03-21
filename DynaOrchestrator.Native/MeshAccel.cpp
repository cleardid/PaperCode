#include "MeshAccel.h"
#include "Geometry.h"
#include "Logger.h"

#include <algorithm>
#include <cmath>
#include <unordered_set>
#include <iostream>

// 整数夹取
static inline int ClampInt(int v, int lo, int hi)
{
	return std::max(lo, std::min(v, hi));
}

// 基于 STL 三角面片构建 Uniform Grid
// 作用：给 LoS 查询提供候选三角面片集合，避免每条线段都扫全 mesh
MeshUniformGrid BuildMeshUniformGrid(const std::vector<Triangle>& mesh, float cell_size)
{
	MeshUniformGrid grid{};
	grid.cell_size = cell_size;

	// 先校验 cell_size，避免后续出现除 0 / 非法网格维度
	if (!(cell_size > 0.0f) || !std::isfinite(cell_size))
	{
		PRINT_ERR << "[Error] BuildMeshUniformGrid invalid cell_size: " << cell_size << std::endl;
		grid.min_x = grid.min_y = grid.min_z = 0.0f;
		grid.max_x = grid.max_y = grid.max_z = 0.0f;
		grid.nx = grid.ny = grid.nz = 0;
		return grid;
	}

	// 空 mesh 直接返回一个最小可用网格
	if (mesh.empty())
	{
		grid.min_x = grid.min_y = grid.min_z = 0.0f;
		grid.max_x = grid.max_y = grid.max_z = 0.0f;
		grid.nx = grid.ny = grid.nz = 1;
		grid.cells.resize(1);
		return grid;
	}

	// 初始化整体包围盒
	grid.min_x = std::min({ mesh[0].v0.x, mesh[0].v1.x, mesh[0].v2.x });
	grid.min_y = std::min({ mesh[0].v0.y, mesh[0].v1.y, mesh[0].v2.y });
	grid.min_z = std::min({ mesh[0].v0.z, mesh[0].v1.z, mesh[0].v2.z });
	grid.max_x = std::max({ mesh[0].v0.x, mesh[0].v1.x, mesh[0].v2.x });
	grid.max_y = std::max({ mesh[0].v0.y, mesh[0].v1.y, mesh[0].v2.y });
	grid.max_z = std::max({ mesh[0].v0.z, mesh[0].v1.z, mesh[0].v2.z });

	// 统计 mesh 全局 AABB
	for (const auto& tri : mesh)
	{
		grid.min_x = std::min(grid.min_x, std::min({ tri.v0.x, tri.v1.x, tri.v2.x }));
		grid.min_y = std::min(grid.min_y, std::min({ tri.v0.y, tri.v1.y, tri.v2.y }));
		grid.min_z = std::min(grid.min_z, std::min({ tri.v0.z, tri.v1.z, tri.v2.z }));
		grid.max_x = std::max(grid.max_x, std::max({ tri.v0.x, tri.v1.x, tri.v2.x }));
		grid.max_y = std::max(grid.max_y, std::max({ tri.v0.y, tri.v1.y, tri.v2.y }));
		grid.max_z = std::max(grid.max_z, std::max({ tri.v0.z, tri.v1.z, tri.v2.z }));
	}

	// 计算网格尺寸
	grid.nx = std::max(1, static_cast<int>(std::ceil((grid.max_x - grid.min_x) / cell_size)));
	grid.ny = std::max(1, static_cast<int>(std::ceil((grid.max_y - grid.min_y) / cell_size)));
	grid.nz = std::max(1, static_cast<int>(std::ceil((grid.max_z - grid.min_z) / cell_size)));

	const size_t mesh_cell_count =
		static_cast<size_t>(grid.nx) *
		static_cast<size_t>(grid.ny) *
		static_cast<size_t>(grid.nz);

	PRINT_LOG << "[Debug] Mesh grid dims: nx=" << grid.nx
		<< ", ny=" << grid.ny
		<< ", nz=" << grid.nz
		<< ", cells=" << mesh_cell_count << std::endl;

	// 防御性保护：防止维度异常或 cell 数过大
	if (mesh_cell_count == 0 || mesh_cell_count > 100000000ULL)
	{
		PRINT_ERR << "[Error] Invalid mesh grid cell count: " << mesh_cell_count << std::endl;
		grid.nx = grid.ny = grid.nz = 0;
		grid.cells.clear();
		return grid;
	}

	grid.cells.resize(mesh_cell_count);

	// 把每个 triangle 按其 AABB 覆盖范围写入网格
	for (int t = 0; t < static_cast<int>(mesh.size()); ++t)
	{
		const auto& tri = mesh[t];

		const float tri_min_x = std::min({ tri.v0.x, tri.v1.x, tri.v2.x });
		const float tri_min_y = std::min({ tri.v0.y, tri.v1.y, tri.v2.y });
		const float tri_min_z = std::min({ tri.v0.z, tri.v1.z, tri.v2.z });
		const float tri_max_x = std::max({ tri.v0.x, tri.v1.x, tri.v2.x });
		const float tri_max_y = std::max({ tri.v0.y, tri.v1.y, tri.v2.y });
		const float tri_max_z = std::max({ tri.v0.z, tri.v1.z, tri.v2.z });

		const int x0 = ClampInt(static_cast<int>((tri_min_x - grid.min_x) / cell_size), 0, grid.nx - 1);
		const int y0 = ClampInt(static_cast<int>((tri_min_y - grid.min_y) / cell_size), 0, grid.ny - 1);
		const int z0 = ClampInt(static_cast<int>((tri_min_z - grid.min_z) / cell_size), 0, grid.nz - 1);

		const int x1 = ClampInt(static_cast<int>((tri_max_x - grid.min_x) / cell_size), 0, grid.nx - 1);
		const int y1 = ClampInt(static_cast<int>((tri_max_y - grid.min_y) / cell_size), 0, grid.ny - 1);
		const int z1 = ClampInt(static_cast<int>((tri_max_z - grid.min_z) / cell_size), 0, grid.nz - 1);

		for (int x = x0; x <= x1; ++x)
		{
			for (int y = y0; y <= y1; ++y)
			{
				for (int z = z0; z <= z1; ++z)
				{
					const int idx = x + y * grid.nx + z * grid.nx * grid.ny;
					grid.cells[idx].push_back(t);
				}
			}
		}
	}

	return grid;
}

// 查询与线段包围盒重叠的候选三角面片
// 注意：这里只做粗筛，真正是否相交由后续几何相交测试决定
std::vector<int> QueryCandidateTriangles(
	const Point& p1,
	const Point& p2,
	const MeshUniformGrid& grid)
{
	std::vector<int> result;

	// 必须先校验网格有效性，避免后续 ClampInt(..., 0, grid.nx - 1) 使用非法上界
	if (grid.cells.empty() || grid.nx <= 0 || grid.ny <= 0 || grid.nz <= 0 || !(grid.cell_size > 0.0f))
		return result;

	const float seg_min_x = std::min(p1.x, p2.x);
	const float seg_min_y = std::min(p1.y, p2.y);
	const float seg_min_z = std::min(p1.z, p2.z);
	const float seg_max_x = std::max(p1.x, p2.x);
	const float seg_max_y = std::max(p1.y, p2.y);
	const float seg_max_z = std::max(p1.z, p2.z);

	const int x0 = ClampInt(static_cast<int>((seg_min_x - grid.min_x) / grid.cell_size), 0, grid.nx - 1);
	const int y0 = ClampInt(static_cast<int>((seg_min_y - grid.min_y) / grid.cell_size), 0, grid.ny - 1);
	const int z0 = ClampInt(static_cast<int>((seg_min_z - grid.min_z) / grid.cell_size), 0, grid.nz - 1);

	const int x1 = ClampInt(static_cast<int>((seg_max_x - grid.min_x) / grid.cell_size), 0, grid.nx - 1);
	const int y1 = ClampInt(static_cast<int>((seg_max_y - grid.min_y) / grid.cell_size), 0, grid.ny - 1);
	const int z1 = ClampInt(static_cast<int>((seg_max_z - grid.min_z) / grid.cell_size), 0, grid.nz - 1);

	std::unordered_set<int> unique_ids;

	for (int x = x0; x <= x1; ++x)
	{
		for (int y = y0; y <= y1; ++y)
		{
			for (int z = z0; z <= z1; ++z)
			{
				const int idx = x + y * grid.nx + z * grid.nx * grid.ny;

				// 防御性检查，避免 idx 异常时直接越界
				if (idx < 0 || idx >= static_cast<int>(grid.cells.size()))
					continue;

				for (int tri_id : grid.cells[idx])
				{
					unique_ids.insert(tri_id);
				}
			}
		}
	}

	result.reserve(unique_ids.size());
	for (int id : unique_ids)
	{
		result.push_back(id);
	}

	return result;
}