#include "Logger.h"
#include <mutex>
#include <iostream>

namespace
{
	std::mutex g_LogMutex;
}

LogCallback g_Logger = nullptr;

extern "C" EXPORT_API void __cdecl SetLogCallback(LogCallback callback)
{
	std::lock_guard<std::mutex> lock(g_LogMutex);
	g_Logger = callback;
}

void DispatchLog(const std::string& message)
{
	std::lock_guard<std::mutex> lock(g_LogMutex);

	if (g_Logger != nullptr)
	{
		// 已绑定 C# 回调：直接转发给托管侧
		g_Logger(message.c_str());
		return;
	}

	// fallback：绝不能再调用 PRINT_LOG / PRINT_ERR，
	// 否则会再次进入 DispatchLog，造成递归。
	if (!message.empty())
	{
		std::cerr << message << std::endl;
	}
	else
	{
		std::cerr << std::endl;
	}
}