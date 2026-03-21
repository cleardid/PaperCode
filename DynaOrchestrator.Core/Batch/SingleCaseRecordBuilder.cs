using DynaOrchestrator.Core.Models;
using System;

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

            double l = config.Explosive.Xc / 1000.0;
            double w = config.Explosive.Yc / 1000.0;
            double h = config.Explosive.Zc / 1000.0;

            if (l <= 0) l = 1.0;
            if (w <= 0) w = 1.0;
            if (h <= 0) h = 1.0;

            double xAbs = config.Explosive.Xc / 1000.0;
            double yAbs = config.Explosive.Yc / 1000.0;
            double zAbs = config.Explosive.Zc / 1000.0;

            return new BatchCaseRecord
            {
                CaseId = caseId,
                DatasetStage = config.Workspace.SingleDatasetStage,
                GeomType = geomType,
                L = l,
                W = w,
                H = h,
                PositionType = "single",
                XNorm = Clamp01(l > 0 ? xAbs / l : 0.5),
                YNorm = Clamp01(w > 0 ? yAbs / w : 0.5),
                ZNorm = Clamp01(h > 0 ? zAbs / h : 0.5),
                ChargeLevel = "single",
                ChargeMass = config.Explosive.W,
                Completed = "0",
                Status = "Pending"
            };
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}