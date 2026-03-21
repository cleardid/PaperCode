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
		int num_nodes;
		int num_edges;
		int time_steps;
		int feature_dim;
		int attr_dim; // 静态属性维度（当前固定为 11）

		int* coo_rows;		// 长度: num_edges
		int* coo_cols;		// 长度: num_edges
		float* coo_weights; // 长度: num_edges

		// 展平的时空特征张量 (N, T, D)
		// 索引: node_idx * (time_steps * feature_dim) + t_idx * feature_dim + d_idx
		float* node_features;
		// 静态属性内存指针，布局:
		// [x, y, z, d, nx, ny, nz, W_cbrt, d_wall, d_edge, d_corner]
		float* node_attrs;
	};

	// 核心导出函数
	EXPORT_API GraphData* GenerateGraph(const char* trhistPath, const char* stlPath, float Rc, float alpha, float Xc, float Yc, float Zc, float W);

	// 强制内存释放函数，防止跨语言内存泄漏
	EXPORT_API void FreeGraphData(GraphData* data);
}

#endif // PI_GNN_GRAPHENGINE_DATASTRUCTS_H