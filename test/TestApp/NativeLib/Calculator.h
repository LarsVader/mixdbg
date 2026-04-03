#pragma once

#ifdef NATIVELIB_EXPORTS
#define NATIVELIB_API __declspec(dllexport)
#else
#define NATIVELIB_API __declspec(dllimport)
#endif

namespace NativeLib
{
    class NATIVELIB_API Calculator
    {
    public:
        static int Add(int a, int b);
        static int Multiply(int a, int b);
    };
}
