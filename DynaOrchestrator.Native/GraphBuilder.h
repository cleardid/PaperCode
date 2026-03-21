//
// Created by tianjia on 2026/3/17.
//

#ifndef PI_GNN_GRAPHENGINE_GRAPHBUILDER_H
#define PI_GNN_GRAPHENGINE_GRAPHBUILDER_H

#include <vector>

struct Triangle;
struct Point;

// 基于空间哈希的邻域搜索与图拓扑生成
// 参数说明：
// nodes: 所有监测点集合
// stl_mesh: 障碍物边界网格
// Rc: 截断半径 (邻接矩阵稀疏化阈值)
// alpha: 物理衰减系数
// out_rows, out_cols, out_weights: 输出的 COO 稀疏矩阵三元组
void BuildGraphWithHashing(const std::vector<Point>& nodes,
	const std::vector<Triangle>& stl_mesh,
	float Rc, float alpha,
	std::vector<int>& out_rows,
	std::vector<int>& out_cols,
	std::vector<float>& out_weights);

#endif // PI_GNN_GRAPHENGINE_GRAPHBUILDER_H
