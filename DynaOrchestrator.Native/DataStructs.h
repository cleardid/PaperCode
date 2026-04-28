//
// Created by tianjia on 2026/3/17.
//

// 统一数据结构与接口声明

#ifndef PI_GNN_GRAPHENGINE_DATASTRUCTS_H
#define PI_GNN_GRAPHENGINE_DATASTRUCTS_H

#ifdef _WIN32
#define EXPORT_API __declspec(dllexport)
#else
#define EXPORT_API __attribute__((visibility("default")))
#endif

extern "C"
{
	// 展平的 C 风格数据结构，严格对应 C# 的 StructLayout
	struct GraphData
	{
		// 节点数量
		int num_nodes;
		// 边数量
		int num_edges;
		// 时间步长
		int time_steps;
		// 特征维度
		int feature_dim;
		// 静态属性维度 对应 node_attrs
		int attr_dim;

		// 边的起点对应的节点 ID
		int* coo_rows;
		// 边的终点对应的节点 ID
		int* coo_cols;
		// 各边的权重值
		float* coo_weights;
		/*
		详细解释：
			上述三个数组都是长度完全相同的一维数组 。它们的长度等于图中边的总数（记为 E）
			这三个数组通过相同的数组下标（Index）严格对齐绑定在一起。假设我们看第 k 个元素：
				coo_rows[k] 代表边的起点节点 ID
				coo_cols[k] 代表边的终点节点 ID
				coo_weights[k] 代表这条从 row[k] 到 col[k] 的边的物理影响权重

		COO（Coordinate Format，坐标格式）是一种专门用于存储稀疏矩阵的高效数据结构
		在处理庞大的物理空间流体或冲击波网格时，节点数量往往高达几十万
		如果使用传统的 N × N 稠密邻接矩阵来记录节点间的连接关系，会瞬间耗尽计算机内存
		为了解决这个问题，项目采用了 COO 格式，它的核心思想是：只记录那些实际存在物理连接的边，而完全忽略没有连接的空白区域
		完美契合了 PyTorch 等现代深度学习框架的底层图计算逻辑
		*/

		// 展平的时空特征张量 (N, T, D)
		// 索引: node_idx * (time_steps * feature_dim) + t_idx * feature_dim + d_idx
		float* node_features;
		/*
		详细解释：
			node_features 实质上存储了所有节点的
		*/

		// 静态属性内存指针，布局:
		// [x, y, z, d, nx, ny, nz, W_cbrt, d_wall, d_edge, d_corner]
		// nx,ny,nz 用于表述 爆炸源指向观测点的“单位方向向量”
		float* node_attrs;
	};

	// 核心导出函数
	EXPORT_API GraphData* GenerateGraph(const char* trhistPath, const char* stlPath, float Rc, float alpha, float Xc, float Yc, float Zc, float W);

	// 强制内存释放函数，防止跨语言内存泄漏
	EXPORT_API void FreeGraphData(GraphData* data);
}

#endif // PI_GNN_GRAPHENGINE_DATASTRUCTS_H