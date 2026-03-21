namespace DynaOrchestrator.Core.Models
{
    public class AppConfig
    {
        public WorkspaceConfig Workspace { get; set; } = new WorkspaceConfig();
        public PipelineConfig Pipeline { get; set; } = new PipelineConfig();
        public ExplosiveParams Explosive { get; set; } = new ExplosiveParams();
        public OtherConfig Other { get; set; } = new OtherConfig();
    }
}