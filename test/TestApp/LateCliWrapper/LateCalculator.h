#pragma once

using namespace System;

namespace LateCliWrapper
{
    public ref class LateCalculator
    {
    public:
        static int Square(int x)
        {
            return x * x;
        }
    };
}
