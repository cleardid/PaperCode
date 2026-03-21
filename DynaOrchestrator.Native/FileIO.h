//
// Created by tianjia on 2026/3/17.
//

// 文件解析方法

#ifndef PI_GNN_GRAPHENGINE_FILEIO_H
#define PI_GNN_GRAPHENGINE_FILEIO_H

#include <vector>
#include <string>

#include "Geometry.h"

// 解析 ASCII 格式的 STL 文件
std::vector<Triangle> ParseSTL(const char* filepath);

// 辅助方法 去除字符串两端的空白字符
static inline void TrimString(std::string& s);

// 解析 trhist 格式文件，并提取 5 维物理特征用于 Euler 方程约束
// 返回值：
// true - 解析成功
// false - 解析失败
// filepath - 文件路径
// stl_mesh - STL 模型数据
// nodes - 包含节点 ID 和初始坐标的集合
// node_features - 展平的特征数组 (N, T, D)
// node_attrs - 节点的静态属性
// num_nodes, time_steps, feature_dim - 维度的引用返回
// attr_dim - 节点静态属性维度
// Xc, Yc, Zc, W - 爆炸源的坐标和当量
bool ParseTrhist(const char* filepath,
	const std::vector<Triangle>& stl_mesh,
	std::vector<Point>& nodes,
	std::vector<float>& node_features,
	std::vector<float>& node_attrs,
	int& num_nodes, int& time_steps, int& feature_dim, int& attr_dim,
	float Xc, float Yc, float Zc, float W);

#endif // PI_GNN_GRAPHENGINE_FILEIO_H