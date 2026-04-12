#ifndef PI_GNN_GRAPHENGINE_LOGGER_H
#define PI_GNN_GRAPHENGINE_LOGGER_H

#include <sstream>
#include <string>
#include <iostream>

#ifdef _WIN32
#define EXPORT_API __declspec(dllexport)
#else
#define EXPORT_API __attribute__((visibility("default")))
#endif

// 1. 定义 __cdecl 格式的回调指针
using LogCallback = void(__cdecl*)(const char* message);
// 2. 导出回调注册接口
extern "C" EXPORT_API void __cdecl SetLogCallback(LogCallback callback);

// 内部派发函数
void DispatchLog(const std::string& message);

// 3. 流式包装类 (巧妙地收集 << 输入，并在对象析构时发送)
class LogStream {
public:
	~LogStream() {
		DispatchLog(oss.str());
	}

	template<typename T>
	LogStream& operator<<(const T& value) {
		oss << value;
		return *this;
	}

	// 吸收并忽略 std::endl，防止换行符引发格式混乱
	LogStream& operator<<(std::ostream& (*)(std::ostream&)) {
		return *this;
	}

private:
	std::ostringstream oss;
};

// 4. 定义宏替换原有输出
#define PRINT_LOG LogStream()
#define PRINT_ERR LogStream()

#endif // PI_GNN_GRAPHENGINE_LOGGER_H