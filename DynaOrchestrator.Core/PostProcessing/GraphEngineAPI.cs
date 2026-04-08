using System.Runtime.InteropServices;

namespace DynaOrchestrator.Core.PostProcessing
{
    // 严格映射 C++ 的内存布局 (新增 attr_dim 和 node_attrs)
    [StructLayout(LayoutKind.Sequential)]
    public struct GraphData
    {
        public int num_nodes;
        public int num_edges;
        public int time_steps;
        public int feature_dim;
        public int attr_dim;     // 节点静态属性当前固定为 11

        public IntPtr coo_rows;
        public IntPtr coo_cols;
        public IntPtr coo_weights;
        public IntPtr node_features;
        public IntPtr node_attrs; // 11维静态属性内存指针
                                  // 布局: [x, y, z, d, nx, ny, nz, W_cbrt, d_wall, d_edge, d_corner]
    }

    public static class GraphEngineAPI
    {
        private const string DllName = "DynaOrchestrator.Native.dll";

        // ================= 新增：日志回调机制 =================
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void LogCallbackDelegate([MarshalAs(UnmanagedType.LPStr)] string message);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetLogCallback(LogCallbackDelegate callback);

        // 必须使用静态变量持有委托，防止被 .NET 垃圾回收器(GC)回收，导致 C++ 访问空指针闪退
        private static LogCallbackDelegate? _logCallbackInstance;

        public static void InitializeLogger(Action<string>? wpfLogger)
        {
            if (wpfLogger == null) return;

            // 实例化委托并保存在静态变量中
            _logCallbackInstance = new LogCallbackDelegate(msg =>
            {
                // 为 C++ 日志加上专门的前缀，并抛给上层 UI
                wpfLogger.Invoke($"[C++ Engine] {msg}");
            });

            // 将函数指针传递给 C++
            SetLogCallback(_logCallbackInstance);
        }
        // =======================================================

        /// <summary>
        /// 核心接口：生成图数据并返回指针
        /// 参数说明：
        /// <param name="trhistPath">LS-DYNA Trhist 文件路径</param>
        /// <param name="stlPath">STL 文件路径</param>
        /// <param name="Rc">截断半径 (m)</param>
        /// <param name="alpha">物理衰减系数</param>
        /// <param name="Xc">爆心 X 坐标 (mm)</param>
        /// <param name="Yc">爆心 Y 坐标 (mm)</param>
        /// <param name="Zc">爆心 Z 坐标 (mm)</param>
        /// <param name="W">炸药当量 (kg)</param>
        /// </summary>
        /// <returns>图数据指针</returns>
        /// 注意：调用方必须使用 FreeGraphData 释放返回的内存
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr GenerateGraph(string trhistPath, string stlPath, float Rc, float alpha, float Xc, float Yc, float Zc, float W);

        /// <summary>
        /// 释放图数据内存
        /// </summary>
        /// <param name="graphDataPtr">图数据指针</param>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FreeGraphData(IntPtr graphDataPtr);
    }
}
