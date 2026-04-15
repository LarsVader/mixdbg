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

        // Recursion — same method appears multiple times in stack trace
        static int Fibonacci(int n);

        // Deep call chain with multiple locals — tests variable inspection
        // and stepping through sequential native calls
        static int SumRange(int start, int end);

        // Throws std::invalid_argument on negative input — tests exception
        // propagation across native → C++/CLI → C# boundary
        static int FactorialOrThrow(int n);

        // Loop with conditional branches — optimizer target (may unroll/vectorize)
        static int CountPrimes(int limit);

    private:
        static bool IsPrime(int n);
        static int AccumulateSum(int current, int end, int accumulator);
    };
}
