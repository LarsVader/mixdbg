#pragma once

#include "../NativeLib/Calculator.h"

using namespace System;

namespace CliWrapper
{
    public ref class ManagedCalculator
    {
    public:
        static int Add(int a, int b)
        {
            return NativeLib::Calculator::Add(a, b);
        }

        static int Multiply(int a, int b)
        {
            return NativeLib::Calculator::Multiply(a, b);
        }

        // Recursion — native Fibonacci produces deep recursive stack
        static int Fibonacci(int n)
        {
            return NativeLib::Calculator::Fibonacci(n);
        }

        // Deep call chain with intermediate locals
        static int SumRange(int start, int end)
        {
            return NativeLib::Calculator::SumRange(start, end);
        }

        // Throws across native → C++/CLI → C# boundary
        static int FactorialOrThrow(int n)
        {
            return NativeLib::Calculator::FactorialOrThrow(n);
        }

        // Loop with branches in native code
        static int CountPrimes(int limit)
        {
            return NativeLib::Calculator::CountPrimes(limit);
        }
    };
}
