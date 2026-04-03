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
    };
}
