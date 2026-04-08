using DynaOrchestrator.Core.Models;
using DynaOrchestrator.Core.PreProcessing;
using System.IO;

namespace DynaOrchestrator.Core.Batch
{
    public static class SingleCaseRecordBuilder
    {
        public static BatchCaseRecord Build(AppConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            string caseId = string.IsNullOrWhiteSpace(config.Pipeline.CaseId)
                ? "single_case_001"
                : config.Pipeline.CaseId;

            string geomType = string.IsNullOrWhiteSpace(config.Workspace.SingleGeomType)
                ? "G1"
                : config.Workspace.SingleGeomType;

            // 单工况模式下，房间尺寸不能从爆点坐标推导。
            // 优先从 STL 包围盒恢复房间尺寸，单位 m。
            double l = 1.0, w = 1.0, h = 1.0;

            var roomSize = TryGetRoomSizeFromStl(config.Pipeline.StlFile);
            if (roomSize.HasValue)
            {
                l = roomSize.Value.L;
                w = roomSize.Value.W;
                h = roomSize.Value.H;
            }

            // 依据炸药半径和当量计算炸药密度（kg/m³）
            // 获取半径 (mm) 和质量 (kg)
            double radius = config.Explosive.Radius;
            double mass = config.Explosive.W;
            // 计算体积 (m³)，注意单位转换
            double volume = (4.0 / 3.0) * Math.PI * Math.Pow(radius / 1000.0, 3);
            // 计算密度 (kg/m³)
            double density = mass / volume;

            return new BatchCaseRecord
            {
                CaseId = caseId,
                DatasetStage = config.Workspace.SingleDatasetStage,
                GeomType = geomType,
                L = l,
                W = w,
                H = h,
                PositionType = "single",
                X = config.Explosive.Xc,
                Y = config.Explosive.Yc,
                Z = config.Explosive.Zc,
                ChargeLevel = "single",
                ChargeMass = config.Explosive.W,
                ChargeDensity = density,
                Completed = "0",
                Status = "Pending"
            };
        }

        private static (double L, double W, double H)? TryGetRoomSizeFromStl(string stlFile)
        {
            if (string.IsNullOrWhiteSpace(stlFile))
                return null;

            string fullPath = Path.GetFullPath(stlFile);
            if (!File.Exists(fullPath))
                return null;

            var triangles = STLParser.ParseBinarySTL(fullPath, logger: null);
            if (triangles == null || triangles.Count == 0)
                return null;

            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;

            foreach (var tri in triangles)
            {
                UpdateBounds(tri.V0);
                UpdateBounds(tri.V1);
                UpdateBounds(tri.V2);
            }

            if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ))
                return null;

            return (maxX - minX, maxY - minY, maxZ - minZ);

            void UpdateBounds(Vector3 p)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        }
    }
}