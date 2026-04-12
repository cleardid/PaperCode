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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallbackDelegate(IntPtr messagePtr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void SetLogCallback(LogCallbackDelegate? callback);

        // 核心修复 1：必须使用静态变量持有当前委托，防止执行期间被 .NET GC 回收
        private static LogCallbackDelegate? _currentLogCallback;

        public static void InitializeLogger(Action<string>? wpfLogger)
        {
            if (wpfLogger == null)
            {
                SetLogCallback(null);
                _currentLogCallback = null;
                return;
            }

            // 核心修复 2：每次工况执行时赋予新的委托（以更新 CaseId 前缀），
            // 并覆盖静态变量。因为外部有 _postProcessGate 锁保证单线程执行，所以是线程安全的。
            _currentLogCallback = new LogCallbackDelegate(messagePtr =>
            {
                string msg = Marshal.PtrToStringAnsi(messagePtr) ?? string.Empty;
                wpfLogger($"[C++ Engine] {msg}");
            });

            SetLogCallback(_currentLogCallback);
        }

        public static void EnsureAvailable()
        {
            // 核心修复 3：彻底删除所有 NativeLibrary.TryLoad 和 NativeLibrary.Free 逻辑。
            // 仅进行文件存在性检查，将 DLL 的生命周期完全交由 .NET CLR 的 P/Invoke 底层管理，避免 OpenMP 崩溃。
            string explicitPath = Path.Combine(AppContext.BaseDirectory, DllName);
            if (!File.Exists(explicitPath))
            {
                throw new DllNotFoundException(
                    $"系统缺失核心计算引擎组件：未找到 C++ 动态链接库，无法加载 Native 引擎：{explicitPath}。请确认已构建并随程序一起部署。");
            }
        }

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
