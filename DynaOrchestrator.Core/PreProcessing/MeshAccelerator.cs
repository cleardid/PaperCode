
namespace DynaOrchestrator.Core.PreProcessing
{
    /// <summary>
    /// 基于 Uniform Grid 的空间射线求交加速器
    /// 时间复杂度从 O(V * T) 降至 O(V * 1)
    /// </summary>
    internal class MeshAccelerator
    {
        private readonly Dictionary<long, List<Triangle>> _grid = new();
        private readonly double _cellSize;
        private readonly BoundingBox _bounds;

        public MeshAccelerator(List<Triangle> mesh, BoundingBox bounds, int resolution = 50)
        {
            _bounds = bounds;

            // 计算最大边长并划分网格
            double maxSpan = Math.Max(bounds.MaxX - bounds.MinX, Math.Max(bounds.MaxY - bounds.MinY, bounds.MaxZ - bounds.MinZ));
            _cellSize = maxSpan / resolution;
            if (_cellSize <= 0) _cellSize = 1.0; // 防御性容错

            foreach (var tri in mesh)
            {
                var minX = Math.Min(tri.V0.X, Math.Min(tri.V1.X, tri.V2.X));
                var maxX = Math.Max(tri.V0.X, Math.Max(tri.V1.X, tri.V2.X));
                var minY = Math.Min(tri.V0.Y, Math.Min(tri.V1.Y, tri.V2.Y));
                var maxY = Math.Max(tri.V0.Y, Math.Max(tri.V1.Y, tri.V2.Y));
                var minZ = Math.Min(tri.V0.Z, Math.Min(tri.V1.Z, tri.V2.Z));
                var maxZ = Math.Max(tri.V0.Z, Math.Max(tri.V1.Z, tri.V2.Z));

                int startX = GetIndex(minX, _bounds.MinX), endX = GetIndex(maxX, _bounds.MinX);
                int startY = GetIndex(minY, _bounds.MinY), endY = GetIndex(maxY, _bounds.MinY);
                int startZ = GetIndex(minZ, _bounds.MinZ), endZ = GetIndex(maxZ, _bounds.MinZ);

                // 将面片引用存入其相交的所有空间体素中
                for (int x = startX; x <= endX; x++)
                    for (int y = startY; y <= endY; y++)
                        for (int z = startZ; z <= endZ; z++)
                        {
                            long hash = GetHash(x, y, z);
                            if (!_grid.TryGetValue(hash, out var list))
                            {
                                list = new List<Triangle>();
                                _grid[hash] = list;
                            }
                            list.Add(tri);
                        }
            }
        }

        private int GetIndex(double value, double min) => (int)((value - min) / _cellSize);

        // 质数空间哈希
        private long GetHash(int x, int y, int z) => ((long)x * 73856093) ^ ((long)y * 19349663) ^ ((long)z * 83492791);

        /// <summary>
        /// 获取 +X 方向射线可能穿过的候选三角形
        /// </summary>
        public IEnumerable<Triangle> GetCandidatesRayX(double ox, double oy, double oz)
        {
            int startX = GetIndex(ox, _bounds.MinX);
            int endX = GetIndex(_bounds.MaxX, _bounds.MinX);
            int y = GetIndex(oy, _bounds.MinY);
            int z = GetIndex(oz, _bounds.MinZ);

            var result = new HashSet<Triangle>();
            for (int x = startX; x <= endX; x++)
            {
                if (_grid.TryGetValue(GetHash(x, y, z), out var list))
                    foreach (var tri in list) result.Add(tri);
            }
            return result;
        }

        /// <summary>
        /// 获取 +Y 方向射线可能穿过的候选三角形
        /// </summary>
        public IEnumerable<Triangle> GetCandidatesRayY(double ox, double oy, double oz)
        {
            int x = GetIndex(ox, _bounds.MinX);
            int startY = GetIndex(oy, _bounds.MinY);
            int endY = GetIndex(_bounds.MaxY, _bounds.MinY);
            int z = GetIndex(oz, _bounds.MinZ);

            var result = new HashSet<Triangle>();
            for (int y = startY; y <= endY; y++)
            {
                if (_grid.TryGetValue(GetHash(x, y, z), out var list))
                    foreach (var tri in list) result.Add(tri);
            }
            return result;
        }

        /// <summary>
        /// 获取 +Z 方向射线可能穿过的候选三角形
        /// </summary>
        public IEnumerable<Triangle> GetCandidatesRayZ(double ox, double oy, double oz)
        {
            int x = GetIndex(ox, _bounds.MinX);
            int y = GetIndex(oy, _bounds.MinY);
            int startZ = GetIndex(oz, _bounds.MinZ);
            int endZ = GetIndex(_bounds.MaxZ, _bounds.MinZ);

            var result = new HashSet<Triangle>();
            for (int z = startZ; z <= endZ; z++)
            {
                if (_grid.TryGetValue(GetHash(x, y, z), out var list))
                    foreach (var tri in list) result.Add(tri);
            }
            return result;
        }
    }
}
