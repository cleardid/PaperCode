# DynaOrchestrator

一个面向**密闭刚壁空间爆炸工况**的数据自动化生成工程。

该项目不是神经网络训练代码本体，而是其上游的数据生产系统：以 **WPF + C#** 作为任务调度与可视化前端，以 **LS-DYNA** 作为外部求解器，以 **C++ Native DLL** 负责高性能后处理与图构建，最终批量生成供时空图学习模型使用的 **NPZ 数据集**。

---

## 1. 项目定位

本项目服务于以下固定研究对象：

- 四周刚壁
- 无门窗
- 无内部障碍物
- 密闭空间爆炸
- 数据来源为 LS-DYNA 仿真

工程目标是把“基础模型 + 工况表”自动转化为“可直接喂给图神经网络/时空代理模型的结构化数据集”。

其核心能力包括：

1. 批量读取工况 CSV；
2. 自动生成单工况运行目录；
3. 根据 STL 与爆源参数生成 tracer/采样点并改写 K 文件；
4. 调用 LS-DYNA 执行求解；
5. 解析 `trhist` 与 `STL`；
6. 构建图结构、节点动态特征、节点静态属性和工程标签；
7. 输出为 `.npz` 文件。

---

## 2. 当前版本说明

当前版本已完成以下整理：

- 已移除“数据质量评估”相关代码与说明，不再包含单独的质量评估模块；
- 已修正 Native 日志在未绑定 C# 回调时的 fallback 行为，避免递归日志调用；
- 后处理部分保持现状不变，继续采用稳健优先的串行保护策略。

因此，当前项目是一套**稳定优先的数据生产系统**，而不是追求最大吞吐的激进并发实现。

---

## 3. 解决方案结构

```text
DynaOrchestrator.sln
├─ DynaOrchestrator.Core/
│  ├─ Batch/
│  ├─ Models/
│  ├─ PreProcessing/
│  ├─ Solver/
│  ├─ PostProcessing/
│  ├─ Utils/
│  └─ PipelineExecutor.cs
│
├─ DynaOrchestrator.Desktop/
│  ├─ MainWindow.xaml
│  ├─ App.xaml
│  └─ ViewModels/
│
└─ DynaOrchestrator.Native/
   ├─ GraphEngineAPI.cpp
   ├─ GraphBuilder.cpp
   ├─ FileIO.cpp
   ├─ Geometry.cpp
   ├─ MeshAccel.cpp
   ├─ Logger.cpp
   └─ DataStructs.h
```

### 3.1 各项目职责

#### `DynaOrchestrator.Core`
核心业务层。负责：

- 批处理调度；
- CSV 读写；
- 配置派生；
- 前处理；
- 求解器调用；
- Native 图引擎封装；
- NPZ 写出。

#### `DynaOrchestrator.Desktop`
WPF 桌面程序。负责：

- 加载/显示 CSV 工况；
- 设置并发参数；
- 启动/停止批处理；
- 展示运行日志与状态。

#### `DynaOrchestrator.Native`
C++ 动态库。负责：

- 解析 `trhist`；
- 解析 STL；
- 构建图拓扑；
- 提取节点动态特征与静态属性；
- 向 C# 返回后处理结果。

---

## 4. 总体执行流程

单个工况的执行主链由 `PipelineExecutor` 负责，批量工况由 `BatchRunner` 并发调度。

总体流程如下：

```text
cases.csv
   ↓
BatchRunner
   ↓
为每个 case 创建标准目录
   ↓
BatchConfigBuilder 生成该 case 的 config.json
   ↓
PipelineExecutor
   ├─ 阶段 1：前处理
   ├─ 阶段 2：LS-DYNA 求解
   └─ 阶段 3：后处理并输出 NPZ
```

### 4.1 阶段 1：前处理

主要由 `AdaptiveMeshGenerator` 完成：

- 解析 STL；
- 恢复房间几何包围盒；
- 根据爆源和边界生成 tracer 点；
- 改写基础 K 文件；
- 输出 `model_out.k` 到运行目录。

### 4.2 阶段 2：求解

主要由 `LsDynaOrchestrator` 完成：

- 调用外部 LS-DYNA 进程；
- 使用每个 case 的 CPU/内存配置；
- 在指定 `run/` 目录执行；
- 监控标准输出；
- 允许取消并终止进程树；
- 检查 `trhist` 是否生成。

### 4.3 阶段 3：后处理

由 `PipelineExecutor + GraphEngineAPI + Native DLL` 完成：

- 读取 `trhist`；
- 读取 `STL`；
- 构建空间图；
- 提取动态场 `(N, T, D)`；
- 提取节点静态物理属性；
- 提取工程响应标签；
- 输出 `.npz`。

说明：当前后处理采用**串行保护**，即便批处理并发运行，图构建阶段仍以稳健优先，不追求最大并发吞吐。

---

## 5. 工作区目录约定

运行时工作区由 `Workspace.RootDir` 指定。推荐目录结构如下：

```text
<WorkspaceRoot>/
├─ base_models/
│  ├─ G1/
│  │  ├─ base.k
│  │  └─ room.stl
│  ├─ G2/
│  │  ├─ base.k
│  │  └─ room.stl
│  └─ G3/
│     ├─ base.k
│     └─ room.stl
│
├─ cases/
│  └─ cases.csv
│
└─ runs/
   └─ <DatasetStage>/
      ├─ <CaseId>/
      │  ├─ input/
      │  ├─ run/
      │  └─ output/
      └─ summary/
```

### 5.1 单工况目录结构

每个工况运行时自动生成：

```text
runs/<DatasetStage>/<CaseId>/
├─ input/
│  ├─ base.k
│  ├─ room.stl
│  └─ config.json
│
├─ run/
│  ├─ model_out.k
│  ├─ trhist
│  └─ 其他 LS-DYNA 原始输出
│
└─ output/
   ├─ <CaseId>.npz
   └─ case_metadata.json
```

含义如下：

- `input/`：当前工况的输入副本与派生配置；
- `run/`：LS-DYNA 实际运行目录；
- `output/`：后处理最终产物目录。

---

## 6. 配置文件 `config.json`

桌面程序启动时，会从**可执行文件同目录**读取 `config.json`。若不存在，则使用默认值。

推荐示例：

```json
{
  "Workspace": {
    "RootDir": "./experiments",
    "CasesCsv": "cases/cases.csv",
    "MaxParallelCases": 2,
    "NcpuPerCase": 4,
    "MemoryPerCase": "400m"
  },
  "Pipeline": {
    "BaseKFile": "base.k",
    "StlFile": "room.stl",
    "OutputKFile": "model_out.k",
    "TrhistFile": "trhist",
    "NpzOutputFile": "dataset_01.npz",
    "LsDynaPath": "D:\\Program Files\\ANSYS Inc\\v231\\ansys\\bin\\winx64\\lsdyna_dp.exe",
    "EnablePreProcessing": true,
    "EnableSimulation": true,
    "EnablePostProcessing": true
  },
  "Explosive": {
    "Xc": 0.0,
    "Yc": 0.0,
    "Zc": 0.0,
    "Radius": 0.0,
    "W": 0.0
  },
  "Other": {
    "Rc": 0.75,
    "Alpha": 1.0,
    "DlDense": 50.0,
    "SparseFactor": 4,
    "CoreRadiusMultiplier": 4.0,
    "WallMargin": 60.0,
    "TrhistDt": 0.00001
  }
}
```

### 6.1 `Workspace`

- `RootDir`：工作区根目录；
- `CasesCsv`：工况 CSV，相对于 `RootDir`；
- `MaxParallelCases`：最大并发工况数；
- `NcpuPerCase`：每个 case 分配给 LS-DYNA 的 CPU 数；
- `MemoryPerCase`：每个 case 分配给 LS-DYNA 的内存参数。

### 6.2 `Pipeline`

- `BaseKFile`：基础 K 文件名；
- `StlFile`：基础 STL 文件名；
- `OutputKFile`：前处理后写出的运行 K 文件名；
- `TrhistFile`：LS-DYNA 输出的时程文件名；
- `NpzOutputFile`：默认 NPZ 文件名占位；
- `LsDynaPath`：LS-DYNA 可执行文件完整路径；
- `EnablePreProcessing`：是否执行前处理；
- `EnableSimulation`：是否执行求解；
- `EnablePostProcessing`：是否执行后处理。

### 6.3 `Explosive`

该段在批处理时会被每条 CSV 记录中的爆点位置、装药质量等参数覆盖。它更像结构占位，而不是固定运行值。

### 6.4 `Other`

- `Rc`：图构建截断半径，单位 **m**；
- `Alpha`：图边权重衰减系数；
- `DlDense`：基础采样尺寸，配置输入单位 **mm**；
- `SparseFactor`：远场稀疏倍率；
- `CoreRadiusMultiplier`：爆心核心区加密倍数；
- `WallMargin`：边界层裕度，配置输入单位 **mm**；
- `TrhistDt`：时程输出间隔，单位 **s**。

注意：`DlDense` 与 `WallMargin` 在执行时会统一从 **mm 转为 m**。

---

## 7. 工况表 `cases.csv`

### 7.1 必填字段

`CaseCsvReader` 当前要求以下字段必须存在：

```csv
CaseId,GeomType,L,W,H,PositionType,X,Y,Z,ChargeLevel,ChargeMass,ChargeDensity,DatasetStage
```

字段说明：

- `CaseId`：工况唯一标识，例如 `G1_P1_C1`
- `GeomType`：几何类型，对应 `base_models/G1`、`base_models/G2` 等
- `L,W,H`：房间长宽高，单位 **m**
- `PositionType`：爆点位置类型说明，例如 `center`
- `X,Y,Z`：爆点**绝对坐标**，单位 **mm**
- `ChargeLevel`：装药等级标签，例如 `C1`
- `ChargeMass`：装药质量，单位 **kg**
- `ChargeDensity`：装药密度，单位 **kg/m3**
- `DatasetStage`：数据阶段，例如 `pilot_54`、`train`、`val`

### 7.2 可选状态字段

程序运行时会自动补充并维护以下字段：

```csv
Completed,Status,LastRunTime
```

含义：

- `Completed`：`1` 表示全流程完成，`0` 表示未完成；
- `Status`：当前状态，如 `Pending / Running / PreProcessed / Simulated / Success / Failed / Canceled`；
- `LastRunTime`：最近一次执行时间。

### 7.3 约束条件

代码当前包含如下校验：

- `CaseId` 不能重复；
- `L/W/H` 必须大于 0；
- `ChargeMass` 必须大于 0；
- `X/Y/Z` 必须位于房间包围盒内；
- `X/Y/Z` 按 **mm** 解释；
- `L/W/H` 按 **m** 解释。

### 7.4 示例

```csv
CaseId,GeomType,L,W,H,PositionType,X,Y,Z,ChargeLevel,ChargeMass,ChargeDensity,DatasetStage
G1_P1_C1,G1,4,4,4,center,2000,2000,2000,C1,0.05,1600,pilot_54
G1_P2_C1,G1,4,4,4,near_wall_x,800,2000,2000,C1,0.05,1600,pilot_54
G2_P1_C2,G2,6,4,3,center,3000,2000,1500,C2,0.10,1600,pilot_54
```

---

## 8. 输出数据说明

后处理成功后，将在：

```text
runs/<DatasetStage>/<CaseId>/output/<CaseId>.npz
```

生成一个 NPZ 文件。

### 8.1 当前 NPZ 内容

根据 `NpzWriter.Save(...)`，当前版本写出的条目包括：

```text
edge_index_row.npy
edge_index_col.npy
edge_weight.npy
x.npy
node_attr.npy
p_max.npy
t_arrival.npy
positive_impulse.npy
positive_duration.npy
near_wall_flag.npy
near_edge_flag.npy
near_corner_flag.npy
sampling_region_id.npy
case_cond.npy
```

### 8.2 字段解释

#### 图结构

- `edge_index_row`
- `edge_index_col`
- `edge_weight`

表示图的 COO 拓扑与边权。

#### 动态特征 `x`

`x.npy` 形状为：

```text
(N, T, D)
```

当前固定 `D = 5`，顺序为：

```text
[rho, vx, vy, vz, overpressure]
```

即：

- `rho`：密度
- `vx, vy, vz`：速度三分量
- `overpressure`：超压

#### 静态属性 `node_attr`

`node_attr.npy` 形状为：

```text
(N, 11)
```

当前顺序为：

```text
[x, y, z, d, nx, ny, nz, W_cbrt, d_wall, d_edge, d_corner]
```

含义：

- `x, y, z`：节点空间坐标
- `d`：节点到爆源距离
- `nx, ny, nz`：源点到节点方向向量
- `W_cbrt`：装药质量立方根尺度
- `d_wall`：到墙面距离
- `d_edge`：到棱边距离
- `d_corner`：到角点距离

#### 工程标签

- `p_max`：峰值超压
- `t_arrival`：到达时刻
- `positive_impulse`：正相比冲
- `positive_duration`：正压持续时间

这些标签由 `EngineeringLabelExtractor` 基于完整时程提取。

#### 语义先验

- `near_wall_flag`
- `near_edge_flag`
- `near_corner_flag`
- `sampling_region_id`

用于显式编码边界附近、棱边附近、角点附近等几何语义。

#### 工况条件向量

`case_cond.npy` 长度固定为 7，当前定义为：

```text
[charge_x_m, charge_y_m, charge_z_m, room_L_m, room_W_m, room_H_m, charge_scale]
```

---

## 9. 运行状态与批处理逻辑

### 9.1 状态恢复

桌面程序加载 CSV 时，会自动将以下状态重置为可重新执行：

- `Running` → `Pending`
- `Failed` → `Pending`

启动批处理前，还会把：

- `Running`
- `Failed`
- `Canceled`

统一重置为 `Pending`。

### 9.2 完成判断

当前工程内，全流程执行成功的典型标志为：

- `Status = Success`
- `Completed = 1`
- `output/<CaseId>.npz` 已存在

如果仅执行到前处理或求解阶段，则状态分别可能为：

- `PreProcessed`
- `Simulated`

此时 `Completed` 不应视为最终完成。

---

## 10. 编译与运行

### 10.1 推荐环境

- Windows x64
- Visual Studio 2022
- .NET 8 SDK
- 已安装 WPF/.NET 桌面开发组件
- 已安装 C++ 桌面开发组件
- 本机已正确安装并可调用 LS-DYNA

### 10.2 编译步骤

1. 使用 Visual Studio 2022 打开 `DynaOrchestrator.sln`；
2. 将解决方案平台切换为 **x64**；
3. 设置 `DynaOrchestrator.Desktop` 为启动项目；
4. 编译整个解决方案。

说明：Native DLL 需要与桌面程序在同一输出目录下可被加载，否则后处理阶段会失败。

### 10.3 运行步骤

1. 准备 `base_models/`、`cases/` 与 `config.json`；
2. 启动 `DynaOrchestrator.Desktop`；
3. 检查工作区路径、CSV 路径、并发参数；
4. 点击“加载 CSV”；
5. 确认工况列表正常显示；
6. 点击“开始”；
7. 观察日志与状态列变化；
8. 如需终止，点击“停止”。

---

## 11. 关键实现说明

### 11.1 前处理

`AdaptiveMeshGenerator` 是前处理核心模块，负责：

- STL 解析与空间包围盒恢复；
- tracer 点生成；
- K 文件改写；
- 生成实际送入 LS-DYNA 的 `model_out.k`。

### 11.2 求解器调度

`LsDynaOrchestrator` 通过外部进程方式调用 LS-DYNA，并支持：

- 标准输出重定向；
- 取消；
- 进程树终止；
- 结果文件检查。

### 11.3 Native 图引擎

`GraphEngineAPI.cpp + GraphBuilder.cpp + FileIO.cpp` 负责：

- 解析仿真结果；
- 构建图边；
- 生成动态特征与静态属性；
- 通过 P/Invoke 将结果返回托管层。

### 11.4 Native 日志

当前版本中，`Logger.cpp` 已修正 fallback 行为：

- 当 C# 已注册回调时，日志发回托管侧；
- 当回调未注册时，直接写入原生控制台流；
- 不再通过日志宏递归调用自身。

---

## 12. 当前工程边界

本项目当前**不负责**以下内容：

- 神经网络训练；
- 模型推理；
- 论文图表自动生成；
- 质量评估报告生成；
- 多场景复杂房间泛化建模。

它的职责边界非常清楚：

> 为密闭刚壁空间爆炸任务稳定地产出结构化 NPZ 数据。

---

## 13. 已知注意事项

1. `L/W/H` 与 `X/Y/Z` 的单位不同：
   - 房间尺寸：**m**
   - 爆点坐标：**mm**

2. `DlDense` 与 `WallMargin` 在配置输入时按 **mm** 给出，但运行时会转换为 **m**。

3. 后处理目前以稳健优先，不建议在未充分验证前移除串行保护。

4. `trhist` 解析依赖当前 LS-DYNA 输出格式，若输出格式改变，需同步检查 Native 与 C# 后处理逻辑。

5. 若 Native DLL 未能复制到桌面程序输出目录，后处理阶段会直接失败。

---

## 14. 适用场景

本项目适合以下用途：

- 批量生成爆炸仿真图数据；
- 为时空图神经网络提供训练/验证样本；
- 研究不同房间尺寸、爆点位置、装药质量对时空响应的影响；
- 构建固定研究对象下的高一致性数据集。

不适合的用途包括：

- 通用 CAD/CAE 前处理平台；
- 任意复杂建筑场景自动建模；
- 通用流体仿真数据平台；
- 高吞吐分布式调度器。

---

## 15. 总结

`DynaOrchestrator` 当前已经形成了一条完整、清晰、可重复的数据生产链：

- 以工况 CSV 驱动；
- 以 WPF 界面管理；
- 以 C# 负责执行编排；
- 以 LS-DYNA 提供求解；
- 以 C++ Native 引擎完成高性能后处理；
- 最终输出为可供后续图学习模型直接使用的 NPZ 数据集。

如果后续工作仍围绕“密闭刚壁空间爆炸数据系统”推进，那么这份 README 所描述的边界、目录约定、数据格式和执行方式，应作为当前工程的统一说明文档。
