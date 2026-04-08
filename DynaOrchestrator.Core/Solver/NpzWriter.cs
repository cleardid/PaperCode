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
            float[] pMax,
            float[] tArrival,
            float[] positiveImpulse,
            float[] positiveDuration,
            float[] nearWallFlag,
            float[] nearEdgeFlag,
            float[] nearCornerFlag,
            int[] samplingRegionId,
            float[] caseCond,
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
    }
}
