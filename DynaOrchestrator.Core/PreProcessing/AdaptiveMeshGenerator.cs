using DynaOrchestrator.Core.Models;
using System.Globalization;

namespace DynaOrchestrator.Core.PreProcessing
{
    /// <summary>
    /// 包围盒边界
    /// </summary>
    public class BoundingBox
    {
        public double MinX = double.MaxValue, MaxX = double.MinValue;
        public double MinY = double.MaxValue, MaxY = double.MinValue;
        public double MinZ = double.MaxValue, MaxZ = double.MinValue;

        /// <summary>
        /// 判断包围盒是否有效
        /// 只有当三个方向都成功写入过点之后，包围盒才有效
        /// </summary>
        public bool IsValid =>
            MinX <= MaxX &&
            MinY <= MaxY &&
            MinZ <= MaxZ &&
            MinX != double.MaxValue &&
            MinY != double.MaxValue &&
            MinZ != double.MaxValue &&
            MaxX != double.MinValue &&
            MaxY != double.MinValue &&
            MaxZ != double.MinValue;

        public void Expand(double x, double y, double z)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;

            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;

            if (z < MinZ) MinZ = z;
            if (z > MaxZ) MaxZ = z;
        }
    }

    /// <summary>
    /// 辅助的整数三元组结构体，用于体素哈希表的键
    /// </summary>
    /// 核心设计：使用 readonly struct 和 IEquatable 接口实现高效的值类型，确保在 HashSet 中正确比较和存储
    public readonly struct Int3 : IEquatable<Int3>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public Int3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(Int3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is Int3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    }

    /// <summary>
    /// 追踪点
    /// </summary>
    /// 核心设计：使用 readonly struct 定义不可变的追踪点结构，确保数据安全和性能优化
    public readonly struct TracerPoint
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public TracerPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public static class AdaptiveMeshGenerator
    {

        // 辅助格式化函数：强制保留1位小数，使用英文小数点，并严格占据10个字符宽度
        private static string Fmt10(double val)
        {
            return val.ToString("F1", CultureInfo.InvariantCulture).PadLeft(10);
        }

        /// <summary>
        /// 优先从 STL 面片直接提取包围盒。
        /// STL 已经在解析阶段完成 mm -> m 转换，因此这里直接使用 m 单位。
        /// </summary>
        /// <param name="stlMesh">STL 三角面片集合</param>
        /// <param name="box">输出包围盒</param>
        /// <returns>提取成功返回 true，否则返回 false</returns>
        private static bool TryBuildBoundingBoxFromStl(List<Triangle> stlMesh, out BoundingBox box)
        {
            box = new BoundingBox();

            if (stlMesh == null || stlMesh.Count == 0)
            {
                return false;
            }

            foreach (var tri in stlMesh)
            {
                box.Expand(tri.V0.X, tri.V0.Y, tri.V0.Z);
                box.Expand(tri.V1.X, tri.V1.Y, tri.V1.Z);
                box.Expand(tri.V2.X, tri.V2.Y, tri.V2.Z);
            }

            return box.IsValid;
        }

        /// <summary>
        /// 从 K 文件的 *NODE 节点中提取包围盒。
        /// 注意：这里会把空气域节点也纳入统计，因此只作为 STL 失败时的回退方案。
        /// K 文件单位为 mm，这里统一转换为 m。
        /// </summary>
        /// <param name="inputKFile">输入 K 文件路径</param>
        /// <param name="box">输出包围盒</param>
        /// <returns>提取成功返回 true，否则返回 false</returns>
        private static bool TryBuildBoundingBoxFromKNodes(string inputKFile, out BoundingBox box)
        {
            box = new BoundingBox();

            if (!File.Exists(inputKFile))
            {
                return false;
            }

            var lines = File.ReadAllLines(inputKFile);

            bool inNode = false;
            double scale = 0.001; // mm -> m

            foreach (var line in lines)
            {
                string tLine = line.Trim();

                if (tLine.StartsWith("*"))
                {
                    inNode = tLine.StartsWith("*NODE", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inNode)
                {
                    continue;
                }

                if (tLine.StartsWith("$") || tLine.Length < 56)
                {
                    continue;
                }

                // LS-DYNA *NODE 格式:
                // nid(8), x(16), y(16), z(16)
                if (double.TryParse(line.Substring(8, 16), out double x) &&
                    double.TryParse(line.Substring(24, 16), out double y) &&
                    double.TryParse(line.Substring(40, 16), out double z))
                {
                    box.Expand(x * scale, y * scale, z * scale);
                }
            }

            return box.IsValid;
        }

        /// <summary>
        /// 统一获取包围盒：优先使用 STL，失败后回退到 K 文件 *NODE。
        /// 这样可以避免优先把空气域节点纳入包围盒，保证房间几何语义更纯净。
        /// </summary>
        /// <param name="inputKFile">输入 K 文件路径</param>
        /// <param name="stlMesh">STL 面片集合</param>
        /// <returns>最终包围盒</returns>
        /// <exception cref="Exception">STL 与 K 文件都无法生成有效包围盒时抛出异常</exception>
        private static BoundingBox BuildBoundingBox(string inputKFile, List<Triangle> stlMesh, Action<string>? logger)
        {
            if (TryBuildBoundingBoxFromStl(stlMesh, out var stlBox))
            {
                logger?.Invoke("[解析] 包围盒来源: STL");
                return stlBox;
            }

            logger?.Invoke("[警告] STL 包围盒提取失败，回退到 K 文件 *NODE 包围盒。");

            if (TryBuildBoundingBoxFromKNodes(inputKFile, out var kBox))
            {
                logger?.Invoke("[解析] 包围盒来源: K(*NODE) 回退");
                return kBox;
            }

            throw new Exception("无法从 STL 或 K 文件中提取有效包围盒。");
        }

        /// <summary>
        /// 依据 K 文件以及 STL 信息生成 图数据
        /// </summary>
        /// <param name="inputKFile">读取的 K 文件</param>
        /// <param name="outputKFile">输出的 K 文件</param>
        /// <param name="stlMesh">STL 模型</param>
        /// <param name="opt">其他配置参数</param>
        /// <param name="exp">爆炸源信息</param>
        /// <returns></returns>
        public static void ProcessAndGenerate(string inputKFile, string outputKFile, List<Triangle> stlMesh, OtherConfig opt, ExplosiveParams exp, Action<string>? logger)
        {
            // 1. 优先基于 STL 提取房间包围盒；若失败则回退到 K 文件 *NODE
            var box = BuildBoundingBox(inputKFile, stlMesh, logger);

            // 2. 读取原始 K 文件内容，后续仍用于重写 *INITIAL_VOLUME_FRACTION_GEOMETRY 和追加 tracer
            var originalLines = File.ReadAllLines(inputKFile).ToList();

            // 输出包围盒与爆炸源信息
            logger?.Invoke($"[解析] 空间包围盒: X[{box.MinX}, {box.MaxX}], Y[{box.MinY}, {box.MaxY}], Z[{box.MinZ}, {box.MaxZ}]");
            logger?.Invoke($"[解析] 爆炸源中心: ({exp.Xc}, {exp.Yc}, {exp.Zc}), 半径: {exp.Radius} mm");

            // 3. 生成自适应测点
            var tracers = GenerateTracers(box, exp, stlMesh, opt, logger);

            // 4. 重写 K 文件
            using (var writer = new StreamWriter(outputKFile, false, System.Text.Encoding.ASCII))
            {
                // 初始化变量
                bool inGeometry = false;
                int geoDataLineCount = 0;

                // 单位转换参数
                double scaleBack = 1000.0; // m -> mm

                foreach (var line in originalLines)
                {
                    string trimmedLine = line.TrimStart();

                    // 拦截原有的 *END，防止解析截断
                    if (trimmedLine.StartsWith("*END", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (trimmedLine.StartsWith("*INITIAL_VOLUME_FRACTION_GEOMETRY", StringComparison.OrdinalIgnoreCase))
                    {
                        inGeometry = true;
                        geoDataLineCount = 0;
                        writer.WriteLine(line);
                        continue;
                    }

                    if (inGeometry && trimmedLine.StartsWith("*"))
                    {
                        inGeometry = false;
                    }

                    // 严格判断注释行，防止错行覆盖
                    if (inGeometry && !trimmedLine.StartsWith("$"))
                    {
                        geoDataLineCount++;
                        if (geoDataLineCount == 3)
                        {
                            // 使用不变区域文化严格对齐 F10.0 格式
                            writer.WriteLine(Fmt10(exp.Xc) + Fmt10(exp.Yc) + Fmt10(exp.Zc) + Fmt10(exp.Radius));
                            continue;
                        }
                    }

                    writer.WriteLine(line);
                }

                // 写入追加的 Tracer
                writer.WriteLine("*DATABASE_TRACER");
                writer.WriteLine("$#    time     track         x         y         z      ammg       nid    radius");
                foreach (var pt in tracers)
                {
                    writer.WriteLine("       0.0         1" + Fmt10(pt.X * scaleBack) + Fmt10(pt.Y * scaleBack) + Fmt10(pt.Z * scaleBack) + "         0         0       0.0");
                }

                writer.WriteLine("*DATABASE_TRHIST");
                writer.WriteLine("$#      dt    binary      lcur     ioopt");
                writer.WriteLine(opt.TrhistDt.ToString("F7", CultureInfo.InvariantCulture).PadLeft(10) + "         0         0         1");

                writer.WriteLine("*END");
            }

            logger?.Invoke($"[前处理] 网格自适应生成完成。保留节点数: {tracers.Count}");
        }

        /// <summary>
        /// 生成自适应测点，核心算法
        /// </summary>
        /// <param name="box">包围盒边界</param>
        /// <param name="exp">爆炸物参数</param>
        /// <param name="stlMesh">STL网格</param>
        /// <param name="opt">其他配置</param>
        /// <returns>测点列表</returns>
        private static List<TracerPoint> GenerateTracers(BoundingBox box, ExplosiveParams exp, List<Triangle> stlMesh, OtherConfig opt, Action<string>? logger)
        {
            // 声明结果
            var tracers = new List<TracerPoint>();

            // 获取转化单位后的爆炸物参数
            double scale = 0.001;
            // mm -> m
            var explosive = new ExplosiveParams
            {
                Xc = exp.Xc * scale,
                Yc = exp.Yc * scale,
                Zc = exp.Zc * scale,
                Radius = exp.Radius * scale,
                W = exp.W
            };

            // 体素采样参数 m
            double dlDense = opt.DlDense;
            int sparseFactor = opt.SparseFactor;
            double coreRadius = explosive.Radius * opt.CoreRadiusMultiplier;
            // 边界层加密参数 m
            double wallMargin = opt.WallMargin;

            // 1. 建立空间体素哈希表，用于快速查询“该区域是否有墙”
            // Tuple<int, int, int> 代表网格的三维索引
            HashSet<Int3> wallVoxels = new HashSet<Int3>();

            foreach (var tri in stlMesh)
            {
                // 使用面片的包围盒映射到体素坐标，略微高估边界是极其安全的工程做法
                int minX = (int)Math.Floor(Math.Min(tri.V0.X, Math.Min(tri.V1.X, tri.V2.X)) / dlDense);
                int maxX = (int)Math.Ceiling(Math.Max(tri.V0.X, Math.Max(tri.V1.X, tri.V2.X)) / dlDense);
                int minY = (int)Math.Floor(Math.Min(tri.V0.Y, Math.Min(tri.V1.Y, tri.V2.Y)) / dlDense);
                int maxY = (int)Math.Ceiling(Math.Max(tri.V0.Y, Math.Max(tri.V1.Y, tri.V2.Y)) / dlDense);
                int minZ = (int)Math.Floor(Math.Min(tri.V0.Z, Math.Min(tri.V1.Z, tri.V2.Z)) / dlDense);
                int maxZ = (int)Math.Ceiling(Math.Max(tri.V0.Z, Math.Max(tri.V1.Z, tri.V2.Z)) / dlDense);

                for (int x = minX; x <= maxX; x++)
                    for (int y = minY; y <= maxY; y++)
                        for (int z = minZ; z <= maxZ; z++)
                            wallVoxels.Add(new Int3(x, y, z));
            }

            // 计算边界层加密需要的体素膨胀层数
            int dilationSteps = (int)Math.Ceiling(wallMargin / dlDense);

            // 2. 遍历空间包围盒生成点阵
            int nx = (int)Math.Ceiling((box.MaxX - box.MinX) / dlDense);
            int ny = (int)Math.Ceiling((box.MaxY - box.MinY) / dlDense);
            int nz = (int)Math.Ceiling((box.MaxZ - box.MinZ) / dlDense);

            for (int i = 0; i <= nx; i++)
            {
                for (int j = 0; j <= ny; j++)
                {
                    for (int k = 0; k <= nz; k++)
                    {
                        double x = box.MinX + i * dlDense;
                        double y = box.MinY + j * dlDense;
                        double z = box.MinZ + k * dlDense;

                        // 先判断该点是否位于封闭 STL 内部
                        var p = new Vector3 { X = x, Y = y, Z = z };
                        if (!IsInsideClosedMesh(p, stlMesh))
                        {
                            continue;
                        }

                        // 判据1：是否在爆源核心区
                        double distToCore = Math.Sqrt(Math.Pow(x - explosive.Xc, 2) + Math.Pow(y - explosive.Yc, 2) + Math.Pow(z - explosive.Zc, 2));
                        if (distToCore <= coreRadius)
                        {
                            tracers.Add(new TracerPoint(x, y, z));
                            continue;
                        }

                        // 计算点所在的体素索引
                        int vx = (int)Math.Floor(x / dlDense), vy = (int)Math.Floor(y / dlDense), vz = (int)Math.Floor(z / dlDense);

                        // 判据2：先做 near-wall 粗筛，再做精判
                        bool nearWallCandidate = false;
                        for (int dx = -dilationSteps; dx <= dilationSteps && !nearWallCandidate; dx++)
                            for (int dy = -dilationSteps; dy <= dilationSteps && !nearWallCandidate; dy++)
                                for (int dz = -dilationSteps; dz <= dilationSteps && !nearWallCandidate; dz++)
                                    if (wallVoxels.Contains(new Int3(vx + dx, vy + dy, vz + dz)))
                                        nearWallCandidate = true;

                        if (nearWallCandidate)
                        {
                            if (IsNearWallAccurate(p, stlMesh, wallMargin))
                            {
                                tracers.Add(new TracerPoint(x, y, z));
                                continue;
                            }
                        }

                        // 判据3：远场稀疏降采样
                        if (i % sparseFactor == 0 && j % sparseFactor == 0 && k % sparseFactor == 0)
                        {
                            tracers.Add(new TracerPoint(x, y, z));
                        }
                    }
                }
            }

            logger?.Invoke($"[采样] 基于 STL 的自适应测点生成完毕，总计保留节点数: {tracers.Count}");
            return tracers;
        }

        /// <summary>
        /// 判断点是否在封闭 STL 网格内部，核心算法：从点向三个轴向发射射线，统计与 STL 的交点数，投票决定内部/外部
        /// </summary>
        /// <param name="p">要判断的点 </param>
        /// <param name="stlMesh">STL 网格 </param>
        /// <returns>如果点在网格内部则返回 true，否则返回 false </returns>
        private static bool IsInsideClosedMesh(Vector3 p, List<Triangle> stlMesh)
        {
            const double eps = 1e-9;

            var px = new Vector3 { X = p.X + eps, Y = p.Y + eps, Z = p.Z + eps };

            var dirX = new Vector3 { X = 1.0, Y = 0.0, Z = 0.0 };
            var dirY = new Vector3 { X = 0.0, Y = 1.0, Z = 0.0 };
            var dirZ = new Vector3 { X = 0.0, Y = 0.0, Z = 1.0 };

            int votes = 0;
            if ((CountRayIntersections(px, dirX, stlMesh) & 1) == 1) votes++;
            if ((CountRayIntersections(px, dirY, stlMesh) & 1) == 1) votes++;
            if ((CountRayIntersections(px, dirZ, stlMesh) & 1) == 1) votes++;

            return votes >= 2;
        }

        /// <summary>
        /// 计算从 origin 点沿 direction 方向发射的射线与 STL 网格的交点数量
        /// </summary>
        /// <param name="origin">射线起点</param>
        /// <param name="direction">射线方向</param>
        /// <param name="stlMesh">STL 网格</param>
        /// <returns>交点数量</returns>
        private static int CountRayIntersections(Vector3 origin, Vector3 direction, List<Triangle> stlMesh)
        {
            int count = 0;

            foreach (var tri in stlMesh)
            {
                if (RayIntersectsTriangle(origin, direction, tri, out double t))
                {
                    if (t > 1e-9)
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 判断射线与三角形是否相交，核心算法：Möller–Trumbore 交点算法
        /// </summary>
        /// <param name="origin">射线起点</param>
        /// <param name="direction">射线方向</param>
        /// <param name="tri">三角形</param>
        /// <param name="t">交点参数 t</param>
        /// <returns>如果相交则返回 true，否则返回 false</returns>
        private static bool RayIntersectsTriangle(Vector3 origin, Vector3 direction, Triangle tri, out double t)
        {
            const double EPSILON = 1e-9;
            t = 0.0;

            var edge1 = new Vector3
            {
                X = tri.V1.X - tri.V0.X,
                Y = tri.V1.Y - tri.V0.Y,
                Z = tri.V1.Z - tri.V0.Z
            };

            var edge2 = new Vector3
            {
                X = tri.V2.X - tri.V0.X,
                Y = tri.V2.Y - tri.V0.Y,
                Z = tri.V2.Z - tri.V0.Z
            };

            var h = Cross(direction, edge2);
            double a = Dot(edge1, h);

            if (a > -EPSILON && a < EPSILON)
                return false;

            double f = 1.0 / a;

            var s = new Vector3
            {
                X = origin.X - tri.V0.X,
                Y = origin.Y - tri.V0.Y,
                Z = origin.Z - tri.V0.Z
            };

            double u = f * Dot(s, h);
            if (u < 0.0 || u > 1.0)
                return false;

            var q = Cross(s, edge1);
            double v = f * Dot(direction, q);
            if (v < 0.0 || u + v > 1.0)
                return false;

            t = f * Dot(edge2, q);
            return t > EPSILON;
        }

        /// <summary>
        /// 向量点积
        /// </summary>
        /// <param name="a">向量 a</param>
        /// <param name="b">向量 b</param>
        /// <returns>点积结果</returns>
        private static double Dot(Vector3 a, Vector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        /// <summary>
        /// 向量叉积
        /// </summary>
        /// <param name="a">向量 a</param>
        /// <param name="b">向量 b</param>
        /// <returns>叉积结果</returns>
        private static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3
            {
                X = a.Y * b.Z - a.Z * b.Y,
                Y = a.Z * b.X - a.X * b.Z,
                Z = a.X * b.Y - a.Y * b.X
            };
        }

        /// <summary>
        /// 向量减法
        /// </summary>
        /// <param name="a">向量 a</param>
        /// <param name="b">向量 b</param>
        /// <returns>减法结果</returns>
        private static Vector3 Subtract(Vector3 a, Vector3 b)
        {
            return new Vector3
            {
                X = a.X - b.X,
                Y = a.Y - b.Y,
                Z = a.Z - b.Z
            };
        }

        /// <summary>
        /// 向量加法
        /// </summary>
        /// <param name="a">向量 a</param>
        /// <param name="b">向量 b</param>
        /// <returns>加法结果</returns>
        private static Vector3 Add(Vector3 a, Vector3 b)
        {
            return new Vector3
            {
                X = a.X + b.X,
                Y = a.Y + b.Y,
                Z = a.Z + b.Z
            };
        }

        /// <summary>
        /// 向量乘以标量
        /// </summary>
        /// <param name="v">向量</param>
        /// <param name="s">标量</param>
        /// <returns>乘法结果</returns>
        private static Vector3 Multiply(Vector3 v, double s)
        {
            return new Vector3
            {
                X = v.X * s,
                Y = v.Y * s,
                Z = v.Z * s
            };
        }

        /// <summary>
        /// 向量长度
        /// </summary>
        /// <param name="v">向量</param>
        /// <returns>向量长度</returns>
        private static double Length(Vector3 v)
        {
            return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }

        /// <summary>
        /// 计算两点之间的距离
        /// </summary>
        /// <param name="a">点 a</param>
        /// <param name="b">点 b</param>
        /// <returns>两点之间的距离</returns>
        private static double Distance(Vector3 a, Vector3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// 判断点是否在 STL 网格的边界上，即判断点是否在任意一个三角形的边上
        /// </summary>
        /// <param name="p">要判断的点</param>
        /// <param name="stlMesh">STL 网格</param>
        /// <param name="wallMargin">边界容差</param>
        /// <returns>如果点在 STL 网格的边界上则返回 true，否则返回 false</returns>
        private static bool IsNearWallAccurate(Vector3 p, List<Triangle> stlMesh, double wallMargin)
        {
            foreach (var tri in stlMesh)
            {
                double dist = PointToTriangleDistance(p, tri);
                if (dist <= wallMargin)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 计算点到三角形的距离
        /// </summary>
        /// <param name="p">点</param>
        /// <param name="tri">三角形</param>
        /// <returns>点到三角形的距离</returns>
        /// 核心算法：基于点到三角形的距离计算，考虑了点在三角形内部、边界上以及外部的不同情况
        private static double PointToTriangleDistance(Vector3 p, Triangle tri)
        {
            Vector3 a = tri.V0;
            Vector3 b = tri.V1;
            Vector3 c = tri.V2;

            Vector3 ab = Subtract(b, a);
            Vector3 ac = Subtract(c, a);
            Vector3 ap = Subtract(p, a);

            double d1 = Dot(ab, ap);
            double d2 = Dot(ac, ap);
            if (d1 <= 0.0 && d2 <= 0.0) return Distance(p, a);

            Vector3 bp = Subtract(p, b);
            double d3 = Dot(ab, bp);
            double d4 = Dot(ac, bp);
            if (d3 >= 0.0 && d4 <= d3) return Distance(p, b);

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                double v = d1 / (d1 - d3);
                Vector3 proj = Add(a, Multiply(ab, v));
                return Distance(p, proj);
            }

            Vector3 cp = Subtract(p, c);
            double d5 = Dot(ab, cp);
            double d6 = Dot(ac, cp);
            if (d6 >= 0.0 && d5 <= d6) return Distance(p, c);

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
            {
                double w = d2 / (d2 - d6);
                Vector3 proj = Add(a, Multiply(ac, w));
                return Distance(p, proj);
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
            {
                Vector3 bc = Subtract(c, b);
                double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                Vector3 proj = Add(b, Multiply(bc, w));
                return Distance(p, proj);
            }

            Vector3 n = Cross(ab, ac);
            double nLen = Length(n);
            if (nLen < 1e-12)
                return Math.Min(Distance(p, a), Math.Min(Distance(p, b), Distance(p, c)));

            return Math.Abs(Dot(ap, n)) / nLen;
        }
    }
}
