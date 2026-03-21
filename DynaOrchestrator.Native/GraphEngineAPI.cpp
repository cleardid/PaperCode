//
// Created by tianjia on 2026/3/17.
//

#include "DataStructs.h"
#include "GraphBuilder.h"
#include "FileIO.h"
#include "Logger.h"

#include <cstdlib>
#include <vector>
#include <iostream>
#include <algorithm>

extern "C"
{
	EXPORT_API GraphData* GenerateGraph(const char* trhistPath, const char* stlPath, float Rc, float alpha, float Xc, float Yc, float Zc, float W)
	{

		try
		{
			PRINT_LOG << "[Info] Calling C++ GraphEngine (Windows Build)..." << std::endl;

			// 获取 stl 模型信息
			const auto stl_mesh = ParseSTL(stlPath);
			if (stl_mesh.empty())
			{
				PRINT_ERR << "[Error] STL mesh is empty. node_attr(11) cannot be constructed." << std::endl;
				return nullptr;
			}

			// 解析 trhist 文件
			std::vector<Point> nodes;
			std::vector<float> node_features;
			std::vector<float> node_attrs;
			int num_nodes = 0, time_steps = 0, feature_dim = 0, attr_dim = 0;

			if (!ParseTrhist(trhistPath, stl_mesh, nodes, node_features, node_attrs,
				num_nodes, time_steps, feature_dim, attr_dim, Xc, Yc, Zc, W))
			{
				return nullptr;
			}

			if ((int)nodes.size() != num_nodes)
			{
				PRINT_ERR << "[Error] nodes.size mismatch. nodes.size=" << nodes.size()
					<< ", num_nodes=" << num_nodes << std::endl;
				return nullptr;
			}

			size_t expected_feature_count =
				static_cast<size_t>(num_nodes) * static_cast<size_t>(time_steps) * static_cast<size_t>(feature_dim);
			size_t expected_attr_count =
				static_cast<size_t>(num_nodes) * static_cast<size_t>(attr_dim);

			if (node_features.size() != expected_feature_count)
			{
				PRINT_ERR << "[Error] node_features.size mismatch. actual=" << node_features.size()
					<< ", expected=" << expected_feature_count << std::endl;
				return nullptr;
			}

			if (node_attrs.size() != expected_attr_count)
			{
				PRINT_ERR << "[Error] node_attrs.size mismatch. actual=" << node_attrs.size()
					<< ", expected=" << expected_attr_count << std::endl;
				return nullptr;
			}

			// 构建图拓扑 (O(N) 复杂度)
			std::vector<int> rows, cols;
			std::vector<float> weights;

			PRINT_LOG << "[Debug] Before BuildGraphWithHashing: "
				<< "num_nodes=" << num_nodes
				<< ", time_steps=" << time_steps
				<< ", feature_dim=" << feature_dim
				<< ", attr_dim=" << attr_dim
				<< ", Rc=" << Rc
				<< ", alpha=" << alpha
				<< ", stl_triangles=" << stl_mesh.size()
				<< std::endl;
			// 2. 调用 O(N) 图构建算法
			BuildGraphWithHashing(nodes, stl_mesh, Rc, alpha, rows, cols, weights);

			PRINT_LOG << "[Debug] After BuildGraphWithHashing: "
				<< "rows=" << rows.size()
				<< ", cols=" << cols.size()
				<< ", weights=" << weights.size()
				<< std::endl;

			if (rows.empty() && cols.empty() && weights.empty())
			{
				PRINT_ERR << "[Error] BuildGraphWithHashing produced empty graph. "
					<< "Please check Rc and graph construction conditions." << std::endl;
				return nullptr;
			}

			if (rows.size() != cols.size() || rows.size() != weights.size())
			{
				PRINT_ERR << "[Error] COO size mismatch. rows=" << rows.size()
					<< ", cols=" << cols.size()
					<< ", weights=" << weights.size() << std::endl;
				return nullptr;
			}

			for (size_t k = 0; k < rows.size(); ++k)
			{
				if (rows[k] < 0 || rows[k] >= num_nodes || cols[k] < 0 || cols[k] >= num_nodes)
				{
					PRINT_ERR << "[Error] COO index out of range at k=" << k
						<< ", row=" << rows[k]
						<< ", col=" << cols[k]
						<< ", num_nodes=" << num_nodes << std::endl;
					return nullptr;
				}
			}

			// 3. 构建并分配 C 风格返回结构 (使用 malloc/new 分配非托管内存)
			// auto data = new GraphData();
			// data->num_nodes = num_nodes;
			// data->num_edges = static_cast<int>(rows.size());
			// data->time_steps = time_steps;
			// data->feature_dim = feature_dim;
			//// 获取静态属性维度
			// data->attr_dim = attr_dim;

			// data->coo_rows = new int[data->num_edges];
			// data->coo_cols = new int[data->num_edges];
			// data->coo_weights = new float[data->num_edges];
			// data->node_features = new float[num_nodes * time_steps * feature_dim];
			// data->node_attrs = new float[num_nodes * data->attr_dim];

			//// 4. 数据拷贝到展平的一维数组
			// std::copy(rows.begin(), rows.end(), data->coo_rows);
			// std::copy(cols.begin(), cols.end(), data->coo_cols);
			// std::copy(weights.begin(), weights.end(), data->coo_weights);
			// std::copy(node_features.begin(), node_features.end(), data->node_features);
			// std::copy(node_attrs.begin(), node_attrs.end(), data->node_attrs);

			size_t edge_count = rows.size();
			size_t feature_count = static_cast<size_t>(num_nodes) * static_cast<size_t>(time_steps) * static_cast<size_t>(feature_dim);
			size_t attr_count = static_cast<size_t>(num_nodes) * static_cast<size_t>(attr_dim);

			auto data = new GraphData();
			data->num_nodes = num_nodes;
			data->num_edges = static_cast<int>(edge_count);
			data->time_steps = time_steps;
			data->feature_dim = feature_dim;
			data->attr_dim = attr_dim;

			data->coo_rows = edge_count ? new int[edge_count] : nullptr;
			data->coo_cols = edge_count ? new int[edge_count] : nullptr;
			data->coo_weights = edge_count ? new float[edge_count] : nullptr;
			data->node_features = feature_count ? new float[feature_count] : nullptr;
			data->node_attrs = attr_count ? new float[attr_count] : nullptr;

			PRINT_LOG << "[Debug] copy rows start" << std::endl;
			if (edge_count)
				std::copy(rows.begin(), rows.end(), data->coo_rows);

			PRINT_LOG << "[Debug] copy cols start" << std::endl;
			if (edge_count)
				std::copy(cols.begin(), cols.end(), data->coo_cols);

			PRINT_LOG << "[Debug] copy weights start" << std::endl;
			if (edge_count)
				std::copy(weights.begin(), weights.end(), data->coo_weights);

			PRINT_LOG << "[Debug] copy node_features start" << std::endl;
			if (feature_count)
				std::copy(node_features.begin(), node_features.end(), data->node_features);

			PRINT_LOG << "[Debug] copy node_attrs start" << std::endl;
			if (attr_count)
				std::copy(node_attrs.begin(), node_attrs.end(), data->node_attrs);

			PRINT_LOG << "[Info] Graph generation complete. Edges: " << data->num_edges << std::endl;

			return data;
		}
		catch (const std::exception& ex)
		{
			PRINT_ERR << "[Error] GenerateGraph exception: " << ex.what() << std::endl;
			return nullptr;
		}
		catch (...)
		{
			PRINT_ERR << "[Error] GenerateGraph unknown native exception." << std::endl;
			return nullptr;
		}
	}

	EXPORT_API void FreeGraphData(GraphData* data)
	{
		if (data)
		{
			delete[] data->coo_rows;
			delete[] data->coo_cols;
			delete[] data->coo_weights;
			delete[] data->node_features;
			delete[] data->node_attrs;
			delete data;

			PRINT_LOG << "[Info] GraphData memory freed." << std::endl;
		}
	}
}
