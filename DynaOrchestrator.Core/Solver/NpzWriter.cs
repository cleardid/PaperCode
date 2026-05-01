using System.IO.Compression;
using System.Runtime.CompilerServices;
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
                // 稀疏矩阵 COO 格式的行索引
                WriteNpy(archive, "edge_index_row.npy", rows, new[] { rows.Length }, "<i4");
                // 稀疏矩阵 COO 格式的列索引
                WriteNpy(archive, "edge_index_col.npy", cols, new[] { cols.Length }, "<i4");
                // 节点间的边权重
                WriteNpy(archive, "edge_weight.npy", weights, new[] { weights.Length }, "<f4");

                // 时空动态特征 三维张量，形状为 (N, T, 5)（N=节点数，T=时间步，5=特征维度）
                WriteNpy(archive, "x.npy", features, new[] { num_nodes, time_steps, feature_dim }, "<f4");

                // 静态物理属性 二维张量，形状为(N, 11)（N=节点数，11=静态属性维度）
                WriteNpy(archive, "node_attr.npy", attrs, new[] { num_nodes, attr_dim }, "<f4");

                // 工程响应标签 (N,)
                // 全时程内的峰值超压
                WriteNpy(archive, "p_max.npy", pMax, new[] { num_nodes }, "<f4");
                // 冲击波首次到达该节点的时间
                WriteNpy(archive, "t_arrival.npy", tArrival, new[] { num_nodes }, "<f4");
                // 正压阶段的累计比冲（超压对时间的积分）
                WriteNpy(archive, "positive_impulse.npy", positiveImpulse, new[] { num_nodes }, "<f4");
                // 正压作用的持续时间
                WriteNpy(archive, "positive_duration.npy", positiveDuration, new[] { num_nodes }, "<f4");

                // 语义分类标志 从 11 维特征中提取出来的二值化标志（0.0 或 1.0），用于告诉神经网络该节点是否靠近特殊几何结构
                // 是否贴近墙壁
                WriteNpy(archive, "near_wall_flag.npy", nearWallFlag, new[] { num_nodes }, "<f4");
                // 是否贴近房间棱边
                WriteNpy(archive, "near_edge_flag.npy", nearEdgeFlag, new[] { num_nodes }, "<f4");
                // 是否贴近房间角落
                WriteNpy(archive, "near_corner_flag.npy", nearCornerFlag, new[] { num_nodes }, "<f4");
                // 一个整数数组（形状为 (N,)），用于直接标明节点所属的物理区域，优先级为：角区 (3) > 棱边区 (2) > 近壁区 (1) > 内部流场 (0)
                WriteNpy(archive, "sampling_region_id.npy", samplingRegionId, new[] { num_nodes }, "<i4");

                // case 级条件向量
                // [charge_x_m, charge_y_m, charge_z_m, room_L_m, room_W_m, room_H_m, charge_scale]
                WriteNpy(archive, "case_cond.npy", caseCond, new[] { 7 }, "<f4");
            }

            logger?.Invoke("[NPZ] 已按 STGNS-v1 最小结构保存: 图结构 / x / node_attr / 语义先验 / case_cond / engineering labels");
            logger?.Invoke($"[后处理] 数据集已序列化保存至: {path}");
        }

        private static void WriteNpy<T>(
            ZipArchive archive,
            string entryName,
            ReadOnlySpan<T> data,
            int[] shape,
            string dtype) where T : unmanaged
        {
            // NPZ 是 ZIP 容器，每个数组写成一个 .npy 条目。
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

            using var stream = entry.Open();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            // 写入 .npy magic string 和版本号：\x93NUMPY v1.0。
            writer.Write(new byte[]
            {
        0x93, 0x4E, 0x55, 0x4D, 0x50, 0x59, 0x01, 0x00
            });

            // 一维 shape 需要写成 "(N,)"，多维写成 "(D0, D1, ...)"。
            string shapeStr = shape.Length == 1
                ? $"({shape[0]},)"
                : $"({string.Join(", ", shape)})";

            string dictStr =
                $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': {shapeStr}, }}";

            // .npy v1.0 要求 header 总长度按 64 字节对齐。
            int padding = 64 - ((10 + dictStr.Length) % 64);
            dictStr += new string(' ', padding) + "\n";

            writer.Write((ushort)dictStr.Length);
            writer.Write(Encoding.ASCII.GetBytes(dictStr));
            writer.Flush();

            // 大数组不能一次性 MemoryMarshal.AsBytes(data)，否则可能超过 int.MaxValue。
            WriteSpanAsBytesChunked(stream, data);
        }

        private static void WriteSpanAsBytesChunked<T>(
            Stream stream,
            ReadOnlySpan<T> data) where T : unmanaged
        {
            int elementSize = Unsafe.SizeOf<T>();

            // 控制单次 byte span 大小，避免超大数组转换时溢出。
            const int maxChunkBytes = 64 * 1024 * 1024;
            int maxChunkElements = Math.Max(1, maxChunkBytes / elementSize);

            int offset = 0;

            while (offset < data.Length)
            {
                int count = Math.Min(maxChunkElements, data.Length - offset);

                // Slice 不复制数据，只创建当前块的视图。
                ReadOnlySpan<T> chunk = data.Slice(offset, count);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(chunk);

                stream.Write(bytes);

                offset += count;
            }
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
