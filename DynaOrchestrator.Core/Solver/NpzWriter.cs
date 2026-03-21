using System.IO.Compression;
using System.Text;

namespace DynaOrchestrator.Core.Solver
{
  public static class NpzWriter
  {
    public static void Save(
        string path,
        int[] rows,
        int[] cols,
        float[] weights,
        float[] features,
        float[] attrs,
        float[] time,
        float[] pMax,
        float[] tArrival,
        float[] positiveImpulse,
        float[] positiveDuration,
        float[] nearWallFlag,
        float[] nearEdgeFlag,
        float[] nearCornerFlag,
        int[] samplingRegionId,
        string engineeringLabelsMetaJson,
        string caseMetadataJson,
        string featureMetaJson,
        string configSnapshotJson,
        string graphMetaJson,
        string qualityReportJson,
        float wallMarginM,
        int num_nodes,
        int time_steps,
        int feature_dim,
        int attr_dim,
        Action<string>? logger)
    {
      string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
      if (!string.IsNullOrWhiteSpace(dir))
        Directory.CreateDirectory(dir);

      if (File.Exists(path)) File.Delete(path);

      using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
      {
        // 图拓扑
        WriteNpy(archive, "edge_index_row.npy", rows, new[] { rows.Length }, "<i4");
        WriteNpy(archive, "edge_index_col.npy", cols, new[] { cols.Length }, "<i4");
        WriteNpy(archive, "edge_weight.npy", weights, new[] { weights.Length }, "<f4");
        WriteTextEntry(archive, "graph_meta.json", graphMetaJson);

        // 时空动态特征 (N, T, D)
        WriteNpy(archive, "x.npy", features, new[] { num_nodes, time_steps, feature_dim }, "<f4");

        // 静态物理属性 (N, attr_dim)
        WriteNpy(archive, "node_attr.npy", attrs, new[] { num_nodes, attr_dim }, "<f4");

        // 时间轴 (T,)
        WriteNpy(archive, "time.npy", time, new[] { time_steps }, "<f4");

        // 工程响应标签 (N,)
        WriteNpy(archive, "p_max.npy", pMax, new[] { num_nodes }, "<f4");
        WriteNpy(archive, "t_arrival.npy", tArrival, new[] { num_nodes }, "<f4");
        WriteNpy(archive, "positive_impulse.npy", positiveImpulse, new[] { num_nodes }, "<f4");
        WriteNpy(archive, "positive_duration.npy", positiveDuration, new[] { num_nodes }, "<f4");

        WriteNpy(archive, "near_wall_flag.npy", nearWallFlag, new[] { num_nodes }, "<f4");
        WriteNpy(archive, "near_edge_flag.npy", nearEdgeFlag, new[] { num_nodes }, "<f4");
        WriteNpy(archive, "near_corner_flag.npy", nearCornerFlag, new[] { num_nodes }, "<f4");
        WriteNpy(archive, "sampling_region_id.npy", samplingRegionId, new[] { num_nodes }, "<i4");

        // 标签元数据（JSON）
        WriteTextEntry(archive, "engineering_labels_meta.json", engineeringLabelsMetaJson);

        // case 级 metadata（JSON）
        WriteTextEntry(archive, "case_metadata.json", caseMetadataJson);

        // 动态特征字段说明（JSON）
        WriteTextEntry(archive, "feature_meta.json", featureMetaJson);

        WriteTextEntry(archive, "config_snapshot.json", configSnapshotJson);
        // 数值质量诊断报告（JSON）
        WriteTextEntry(archive, "quality_report.json", qualityReportJson);

        // 静态属性字段说明（JSON）
        WriteTextEntry(archive, "node_attr_meta.json", BuildNodeAttrMetaJson(attr_dim));

        // 近壁/棱边/角区语义字段说明（JSON）
        WriteTextEntry(archive, "sampling_semantics_meta.json", BuildSamplingSemanticsMetaJson(wallMarginM));
      }

      logger?.Invoke("[NPZ] 已保存工程标签: p_max, t_arrival, positive_impulse, positive_duration");
      logger?.Invoke("[NPZ] 已保存标签元数据: engineering_labels_meta.json");
      logger?.Invoke("[NPZ] 已保存 case 级元数据: case_metadata.json");
      logger?.Invoke("[NPZ] 已保存动态图字段元数据: feature_meta.json");
      logger?.Invoke("[NPZ] 已保存配置快照: config_snapshot.json");
      logger?.Invoke("[NPZ] 已保存数值质量诊断报告: quality_report.json");
      logger?.Invoke("[NPZ] 已保存静态属性元数据: node_attr_meta.json");
      logger?.Invoke($"[后处理] 数据集已序列化保存至: {path}");
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
      var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
      using var stream = entry.Open();
      using var writer = new StreamWriter(stream, Encoding.UTF8);
      writer.Write(content ?? string.Empty);
    }

    private static void WriteNpy<T>(ZipArchive archive, string entryName, T[] data, int[] shape, string dtype) where T : unmanaged
    {
      var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
      using (var stream = entry.Open())
      using (var writer = new BinaryWriter(stream))
      {
        // NPY Magic Header
        writer.Write(new byte[] { 0x93, 0x4E, 0x55, 0x4D, 0x50, 0x59, 0x01, 0x00 });

        string shapeStr = shape.Length == 1 ? $"({shape[0]},)" : $"({string.Join(", ", shape)})";
        string dictStr = $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': {shapeStr}, }}";

        int padding = 64 - ((10 + dictStr.Length) % 64);
        dictStr += new string(' ', padding) + "\n";

        writer.Write((ushort)dictStr.Length);
        writer.Write(Encoding.ASCII.GetBytes(dictStr));

        byte[] byteData = new byte[data.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        Buffer.BlockCopy(data, 0, byteData, 0, byteData.Length);
        writer.Write(byteData);
      }
    }

    /// <summary>
    /// 构建节点属性元数据 JSON 字符串
    /// </summary>
    /// <param name="attrDim">节点维度</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static string BuildNodeAttrMetaJson(int attrDim)
    {
      if (attrDim != 11)
        throw new InvalidOperationException($"node_attr 元数据仅支持 11 维，当前 attr_dim={attrDim}");

      return """
                    {
                      "name": "node_attr",
                      "shape": ["num_nodes", 11],
                      "fields": [
                        { "index": 0, "name": "x", "unit": "m" },
                        { "index": 1, "name": "y", "unit": "m" },
                        { "index": 2, "name": "z", "unit": "m" },
                        { "index": 3, "name": "d", "unit": "m" },
                        { "index": 4, "name": "nx", "unit": "1" },
                        { "index": 5, "name": "ny", "unit": "1" },
                        { "index": 6, "name": "nz", "unit": "1" },
                        { "index": 7, "name": "W_cbrt", "unit": "kg^(1/3)" },
                        { "index": 8, "name": "d_wall", "unit": "m" },
                        { "index": 9, "name": "d_edge", "unit": "m" },
                        { "index": 10, "name": "d_corner", "unit": "m" }
                      ]
                    }
                   """;
    }

    private static string BuildSamplingSemanticsMetaJson(float wallMarginM)
    {
      string wallMarginStr = wallMarginM.ToString(System.Globalization.CultureInfo.InvariantCulture);

      return $$"""
                {
                  "name": "sampling_semantics",
                  "wall_margin_m": {{wallMarginStr}},
                  "fields": [
                    {
                      "file": "near_wall_flag.npy",
                      "dtype": "float32",
                      "shape": ["num_nodes"],
                      "definition": "1 if d_wall <= wall_margin_m else 0"
                    },
                    {
                      "file": "near_edge_flag.npy",
                      "dtype": "float32",
                      "shape": ["num_nodes"],
                      "definition": "1 if d_edge <= wall_margin_m else 0"
                    },
                    {
                      "file": "near_corner_flag.npy",
                      "dtype": "float32",
                      "shape": ["num_nodes"],
                      "definition": "1 if d_corner <= wall_margin_m else 0"
                    },
                    {
                      "file": "sampling_region_id.npy",
                      "dtype": "int32",
                      "shape": ["num_nodes"],
                      "definition": "0=regular, 1=near_wall, 2=near_edge, 3=near_corner"
                    }
                  ]
                }
                """;
    }

    public static string BuildGraphMetaJson(int edgeCount, double rc, double alpha)
    {
      return $$"""
    {
      "name": "graph_topology",
      "edge_storage": "directed COO",
      "edge_count": {{edgeCount}},
      "edge_count_semantics": "number of directed edges stored in edge_index_row.npy and edge_index_col.npy",
      "files": [
        { "name": "edge_index_row.npy", "dtype": "int32", "shape": ["num_edges"] },
        { "name": "edge_index_col.npy", "dtype": "int32", "shape": ["num_edges"] },
        { "name": "edge_weight.npy", "dtype": "float32", "shape": ["num_edges"] }
      ],
      "weight_definition": "exp(-alpha * distance)",
      "weight_role": "heuristic spatial proximity weight rather than physical propagation coefficient",
      "parameters": {
        "rc_m": {{rc.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
        "alpha": {{alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
      }
    }
    """;
    }

  }
}
