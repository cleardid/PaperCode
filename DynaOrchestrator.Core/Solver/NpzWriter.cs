using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DynaOrchestrator.Core.Solver
{
    public static class NpzWriter
    {
        public static void Save(
                    string path,
                    ReadOnlySpan<int> rows,
                    ReadOnlySpan<int> cols,
                    ReadOnlySpan<float> weights,
                    ReadOnlySpan<float> features,
                    ReadOnlySpan<float> attrs,
                    ReadOnlySpan<float> pMax,
                    ReadOnlySpan<float> tArrival,
                    ReadOnlySpan<float> positiveImpulse,
                    ReadOnlySpan<float> positiveDuration,
                    ReadOnlySpan<float> nearWallFlag,
                    ReadOnlySpan<float> nearEdgeFlag,
                    ReadOnlySpan<float> nearCornerFlag,
                    ReadOnlySpan<int> samplingRegionId,
                    ReadOnlySpan<float> caseCond,
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

                // 时空动态特征 (N, T, 5)
                WriteNpy(archive, "x.npy", features, new[] { num_nodes, time_steps, feature_dim }, "<f4");

                // 静态物理属性 (N, 11)
                WriteNpy(archive, "node_attr.npy", attrs, new[] { num_nodes, attr_dim }, "<f4");

                // 工程响应标签 (N,)
                WriteNpy(archive, "p_max.npy", pMax, new[] { num_nodes }, "<f4");
                WriteNpy(archive, "t_arrival.npy", tArrival, new[] { num_nodes }, "<f4");
                WriteNpy(archive, "positive_impulse.npy", positiveImpulse, new[] { num_nodes }, "<f4");
                WriteNpy(archive, "positive_duration.npy", positiveDuration, new[] { num_nodes }, "<f4");

                // 语义先验
                WriteNpy(archive, "near_wall_flag.npy", nearWallFlag, new[] { num_nodes }, "<f4");
                WriteNpy(archive, "near_edge_flag.npy", nearEdgeFlag, new[] { num_nodes }, "<f4");
                WriteNpy(archive, "near_corner_flag.npy", nearCornerFlag, new[] { num_nodes }, "<f4");
                WriteNpy(archive, "sampling_region_id.npy", samplingRegionId, new[] { num_nodes }, "<i4");

                // case 级条件向量
                // [charge_x_m, charge_y_m, charge_z_m, room_L_m, room_W_m, room_H_m, charge_scale]
                WriteNpy(archive, "case_cond.npy", caseCond, new[] { 7 }, "<f4");
            }

            logger?.Invoke("[NPZ] 已按 STGNS-v1 最小结构保存: 图结构 / x / node_attr / 语义先验 / case_cond / engineering labels");
            logger?.Invoke($"[后处理] 数据集已序列化保存至: {path}");
        }

        private static void WriteNpy<T>(ZipArchive archive, string entryName, ReadOnlySpan<T> data, int[] shape, string dtype) where T : unmanaged
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            // 使用 leaveOpen: true 确保 Writer 不会关闭底层流，避免压缩流截断
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            // NPY Magic Header
            writer.Write(new byte[] { 0x93, 0x4E, 0x55, 0x4D, 0x50, 0x59, 0x01, 0x00 });

            string shapeStr = shape.Length == 1 ? $"({shape[0]},)" : $"({string.Join(", ", shape)})";
            string dictStr = $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': {shapeStr}, }}";

            int padding = 64 - ((10 + dictStr.Length) % 64);
            dictStr += new string(' ', padding) + "\n";

            writer.Write((ushort)dictStr.Length);
            writer.Write(Encoding.ASCII.GetBytes(dictStr));

            // 必须在写入裸字节前刷新缓冲
            writer.Flush();

            // 零拷贝核心：将泛型内存块直接映射为 Byte Span 并写入底层压缩流
            ReadOnlySpan<byte> byteData = MemoryMarshal.AsBytes(data);
            stream.Write(byteData);
        }

        public static void ValidateBasicStructure(string npzPath, Action<string>? logger = null)
        {
            if (string.IsNullOrWhiteSpace(npzPath))
                throw new ArgumentException("NPZ 路径不能为空。", nameof(npzPath));

            if (!File.Exists(npzPath))
                throw new FileNotFoundException("未找到 NPZ 文件。", npzPath);

            var fileInfo = new FileInfo(npzPath);
            if (fileInfo.Length <= 0)
                throw new InvalidDataException($"NPZ 文件为空：{npzPath}");

            string[] requiredEntries =
            {
                "edge_index_row.npy",
                "edge_index_col.npy",
                "edge_weight.npy",
                "x.npy",
                "node_attr.npy",
                "p_max.npy",
                "t_arrival.npy",
                "positive_impulse.npy",
                "positive_duration.npy",
                "near_wall_flag.npy",
                "near_edge_flag.npy",
                "near_corner_flag.npy",
                "sampling_region_id.npy",
                "case_cond.npy"
            };

            using var archive = ZipFile.OpenRead(npzPath);

            foreach (string entryName in requiredEntries)
            {
                var entry = archive.GetEntry(entryName);
                if (entry == null)
                    throw new InvalidDataException($"NPZ 缺少必需条目：{entryName}");

                if (entry.Length <= 0)
                    throw new InvalidDataException($"NPZ 条目为空：{entryName}");
            }

            logger?.Invoke($"[NPZ] 最小结构校验通过：{npzPath}");
        }
    }
}
