//
// Created by tianjia on 2026/3/17.
//

#include "GraphBuilder.h"
#include "MeshAccel.h"
#include "Geometry.h"
#include "Logger.h"

#include <cmath>
#include <vector>
#include <algorithm>
#include <iostream>
#include <chrono>
#include <thread>
#include <atomic>
#include <omp.h>
#include <iomanip>

// 基于空间哈希的邻域搜索与图拓扑生成
// 当前版本重点：
// 1. 补 Rc / alpha / 网格尺寸 等输入合法性校验
// 2. 补 mesh_accel 有效性校验
// 3. 补 node.id 范围校验
// 4. 把局部 COO 校验放到线程循环结束后
// 5. 把最终输出 COO 校验放到 parallel 外
// 6. 调试阶段先固定单线程，先排除并发因素
void BuildGraphWithHashing(const std::vector<Point>& nodes,
	const std::vector<Triangle>& stl_mesh,
	float Rc, float alpha,
	std::vector<int>& out_rows,
	std::vector<int>& out_cols,
	std::vector<float>& out_weights)
{
	// 先清空输出，避免调用方复用旧数据时造成干扰
	out_rows.clear();
	out_cols.clear();
	out_weights.clear();

	const int num_nodes = static_cast<int>(nodes.size());
	if (num_nodes == 0)
	{
		PRINT_ERR << "[Error] BuildGraphWithHashing: nodes is empty." << std::endl;
		return;
	}

	// Rc 是空间哈希网格和邻域截断半径的核心参数，必须有效
	if (!(Rc > 0.0f) || !std::isfinite(Rc))
	{
		PRINT_ERR << "[Error] BuildGraphWithHashing invalid Rc: " << Rc << std::endl;
		return;
	}

	// alpha 参与 exp(-alpha * dist)，必须是有限值
	if (!std::isfinite(alpha))
	{
		PRINT_ERR << "[Error] BuildGraphWithHashing invalid alpha: " << alpha << std::endl;
		return;
	}

	// 防御性校验：node.id 必须在 [0, num_nodes) 范围内
	// 因为后面写边时使用的是 p1.id / p2.id，而不是 i / j
	for (int i = 0; i < num_nodes; ++i)
	{
		if (nodes[i].id < 0 || nodes[i].id >= num_nodes)
		{
			PRINT_ERR << "[Error] Invalid node id at i=" << i
				<< ", id=" << nodes[i].id
				<< ", num_nodes=" << num_nodes << std::endl;
			return;
		}
	}

	// 1. 计算节点全局包围盒
	float min_x = nodes[0].x, min_y = nodes[0].y, min_z = nodes[0].z;
	float max_x = nodes[0].x, max_y = nodes[0].y, max_z = nodes[0].z;

	for (const auto& p : nodes)
	{
		if (p.x < min_x)
			min_x = p.x;
		if (p.x > max_x)
			max_x = p.x;
		if (p.y < min_y)
			min_y = p.y;
		if (p.y > max_y)
			max_y = p.y;
		if (p.z < min_z)
			min_z = p.z;
		if (p.z > max_z)
			max_z = p.z;
	}

	// 2. 构建节点空间哈希网格
	const float cell_size = Rc;

	const int nx = std::max(1, static_cast<int>(std::ceil((max_x - min_x) / cell_size)));
	const int ny = std::max(1, static_cast<int>(std::ceil((max_y - min_y) / cell_size)));
	const int nz = std::max(1, static_cast<int>(std::ceil((max_z - min_z) / cell_size)));

	const size_t grid_cell_count =
		static_cast<size_t>(nx) *
		static_cast<size_t>(ny) *
		static_cast<size_t>(nz);

	PRINT_LOG << "[Debug] Hash grid dims: nx=" << nx
		<< ", ny=" << ny
		<< ", nz=" << nz
		<< ", cells=" << grid_cell_count << std::endl;

	// 防止网格尺寸异常或过大
	if (grid_cell_count == 0 || grid_cell_count > 100000000ULL)
	{
		PRINT_ERR << "[Error] Invalid hash grid cell count: " << grid_cell_count << std::endl;
		return;
	}

	// 注意：这里必须用 grid_cell_count，不能再写 nx * ny * nz
	std::vector<std::vector<int>> grid(grid_cell_count);

	// 把节点按所属网格单元写入哈希表
	for (int i = 0; i < num_nodes; ++i)
	{
		int cx = std::min(nx - 1, std::max(0, static_cast<int>((nodes[i].x - min_x) / cell_size)));
		int cy = std::min(ny - 1, std::max(0, static_cast<int>((nodes[i].y - min_y) / cell_size)));
		int cz = std::min(nz - 1, std::max(0, static_cast<int>((nodes[i].z - min_z) / cell_size)));

		const int cell_idx = cx + cy * nx + cz * nx * ny;
		grid[cell_idx].push_back(i);
	}

	const float Rc2 = Rc * Rc;

	// 预估边数，减少 vector 动态扩容次数
	const size_t estimated_directed_edges = static_cast<size_t>(num_nodes) * 24;
	out_rows.reserve(estimated_directed_edges);
	out_cols.reserve(estimated_directed_edges);
	out_weights.reserve(estimated_directed_edges);

	// 3. 为 STL 建立 Uniform Grid，用于加速 LoS 查询
	MeshUniformGrid mesh_accel = BuildMeshUniformGrid(stl_mesh, Rc);

	// mesh_accel 必须有效，否则 LoS 查询内部可能继续使用非法网格
	if (mesh_accel.cells.empty() || mesh_accel.nx <= 0 || mesh_accel.ny <= 0 || mesh_accel.nz <= 0)
	{
		PRINT_ERR << "[Error] Invalid mesh acceleration grid." << std::endl;
		return;
	}

	//// 调试阶段：先固定单线程，先排除并发因素
	//// 等确认单线程稳定后，再恢复多线程
	// omp_set_num_threads(1);

#pragma omp parallel
	{
		// 线程局部缓存，避免跨线程直接写共享 vector
		std::vector<int> local_rows;
		std::vector<int> local_cols;
		std::vector<float> local_weights;

		const int max_threads = omp_get_max_threads();
		const size_t per_thread_estimated =
			std::max<size_t>(1024, estimated_directed_edges / std::max(1, max_threads));

		local_rows.reserve(per_thread_estimated);
		local_cols.reserve(per_thread_estimated);
		local_weights.reserve(per_thread_estimated);

#pragma omp for schedule(dynamic, 64)
		for (int i = 0; i < num_nodes; ++i)
		{
			const Point& p1 = nodes[i];

			int cx = std::min(nx - 1, std::max(0, static_cast<int>((p1.x - min_x) / cell_size)));
			int cy = std::min(ny - 1, std::max(0, static_cast<int>((p1.y - min_y) / cell_size)));
			int cz = std::min(nz - 1, std::max(0, static_cast<int>((p1.z - min_z) / cell_size)));

			// 遍历当前节点所在单元及其周围 26 个邻居单元
			for (int dx = -1; dx <= 1; ++dx)
			{
				for (int dy = -1; dy <= 1; ++dy)
				{
					for (int dz = -1; dz <= 1; ++dz)
					{
						const int nx_idx = cx + dx;
						const int ny_idx = cy + dy;
						const int nz_idx = cz + dz;

						if (nx_idx < 0 || nx_idx >= nx ||
							ny_idx < 0 || ny_idx >= ny ||
							nz_idx < 0 || nz_idx >= nz)
						{
							continue;
						}

						const int cell_idx = nx_idx + ny_idx * nx + nz_idx * nx * ny;

						for (int j : grid[cell_idx])
						{
							// 只计算 j > i，避免重复；无向边通过双向写入实现
							if (j <= i)
								continue;

							const Point& p2 = nodes[j];

							const float dx_val = p1.x - p2.x;
							const float dy_val = p1.y - p2.y;
							const float dz_val = p1.z - p2.z;
							const float dist2 = dx_val * dx_val + dy_val * dy_val + dz_val * dz_val;

							if (dist2 > Rc2)
								continue;

							// LoS 作为几何可见性约束，避免隔墙误连边
							if (!IsLineOfSightClear(p1, p2, stl_mesh, mesh_accel))
								continue;

							const float dist = std::sqrt(dist2);
							const float weight = std::exp(-alpha * dist);

							// 双向写边，生成对称 COO
							local_rows.push_back(p1.id);
							local_cols.push_back(p2.id);
							local_weights.push_back(weight);

							local_rows.push_back(p2.id);
							local_cols.push_back(p1.id);
							local_weights.push_back(weight);
						}
					}
				}
			}
		}

		// 线程局部结果一致性校验
		if (local_rows.size() != local_cols.size() || local_rows.size() != local_weights.size())
		{
			//PRINT_ERR << "[Error] Local COO size mismatch. thread local_rows=" << local_rows.size()
			//		  << ", local_cols=" << local_cols.size()
			//		  << ", local_weights=" << local_weights.size() << std::endl;
		}
		else
		{
			// 合并线程局部结果到总输出
#pragma omp critical
			{
				out_rows.reserve(out_rows.size() + local_rows.size());
				out_cols.reserve(out_cols.size() + local_cols.size());
				out_weights.reserve(out_weights.size() + local_weights.size());

				out_rows.insert(out_rows.end(), local_rows.begin(), local_rows.end());
				out_cols.insert(out_cols.end(), local_cols.begin(), local_cols.end());
				out_weights.insert(out_weights.end(), local_weights.begin(), local_weights.end());
			}
		}
	}

	// 4. parallel 结束后，再做总输出一致性校验
	if (out_rows.size() != out_cols.size() || out_rows.size() != out_weights.size())
	{
		PRINT_ERR << "[Error] Output COO size mismatch. rows=" << out_rows.size()
			<< ", cols=" << out_cols.size()
			<< ", weights=" << out_weights.size() << std::endl;

		out_rows.clear();
		out_cols.clear();
		out_weights.clear();
		return;
	}

	PRINT_LOG << "[Debug] BuildGraphWithHashing complete: rows=" << out_rows.size()
		<< ", cols=" << out_cols.size()
		<< ", weights=" << out_weights.size() << std::endl;
}