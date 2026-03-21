
namespace DynaOrchestrator.Core.PreProcessing
{
    /// <summary>
    /// 三维坐标点
    /// </summary>
    public struct Vector3
    {
        public double X, Y, Z;
    }

    /// <summary>
    /// 三角形 用于读取 STL 模型
    /// </summary>
    public struct Triangle
    {
        public Vector3 V0, V1, V2;
    }

    public static class STLParser
    {
        /// <summary>
        /// 解析二进制 STL 文件，返回三角形列表
        /// 注意：STL 文件中的坐标单位通常为 mm，解析时会转换为 m 以保持与后续处理的一致性
        /// </summary>
        /// <param name="filePath">STL 文件路径</param>
        /// <returns>三角形列表</returns>
        /// <exception cref="Exception">解析失败时抛出异常</exception>
        public static List<Triangle> ParseBinarySTL(string filePath, Action<string>? logger)
        {
            try
            {
                var triangles = new List<Triangle>();
                using (var reader = new BinaryReader(File.Open(filePath, FileMode.Open, FileAccess.Read)))
                {
                    // 跳过 80 字节的 ASCII Header
                    reader.ReadBytes(80);

                    // 读取三角形总数 (4 bytes, uint32)
                    uint triangleCount = reader.ReadUInt32();

                    // 单位转换参数
                    float scale = 0.001f; // mm -> m

                    for (uint i = 0; i < triangleCount; i++)
                    {
                        // 跳过法向量 (12 bytes)
                        reader.ReadBytes(12);

                        // 读取 3 个顶点，每个顶点 3 个 float (36 bytes)
                        var v0 = new Vector3 { X = reader.ReadSingle() * scale, Y = reader.ReadSingle() * scale, Z = reader.ReadSingle() * scale };
                        var v1 = new Vector3 { X = reader.ReadSingle() * scale, Y = reader.ReadSingle() * scale, Z = reader.ReadSingle() * scale };
                        var v2 = new Vector3 { X = reader.ReadSingle() * scale, Y = reader.ReadSingle() * scale, Z = reader.ReadSingle() * scale };

                        // 跳过属性字节 (2 bytes)
                        reader.ReadUInt16();

                        triangles.Add(new Triangle { V0 = v0, V1 = v1, V2 = v2 });
                    }
                }

                logger?.Invoke($"[解析] 成功读取二进制 STL，共计 {triangles.Count} 个面片。");
                return triangles;
            }
            catch (Exception e)
            {
                throw new Exception("解析 STL 文件失败：" + e.Message);
            }
        }
    }
}
