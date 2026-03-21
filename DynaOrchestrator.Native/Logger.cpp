#include "Logger.h"

LogCallback g_Logger = nullptr;

extern "C" EXPORT_API void SetLogCallback(LogCallback callback)
{
	g_Logger = callback;
}

void DispatchLog(const std::string &message)
{
	if (g_Logger != nullptr)
	{
		// 通过函数指针将字符串传给 C#
		g_Logger(message.c_str());
	}
	else
	{
		// 如果 C# 还没绑定回调，回退到原生控制台输出
		PRINT_LOG << message << std::endl;
	}
}