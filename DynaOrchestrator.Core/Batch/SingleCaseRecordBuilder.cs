using DynaOrchestrator.Core.Models;

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

            // 计算房间尺寸（m）
            double l = config.Explosive.Xc / 1000.0;
            double w = config.Explosive.Yc / 1000.0;
            double h = config.Explosive.Zc / 1000.0;

            if (l <= 0) l = 1.0;
            if (w <= 0) w = 1.0;
            if (h <= 0) h = 1.0;

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
    }
}