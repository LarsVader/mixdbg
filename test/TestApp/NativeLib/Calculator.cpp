#include "Calculator.h"
#include <stdexcept> // for FactorialOrThrow
namespace NativeLib
{
    int Calculator::Add(int a, int b)
    {
        return a + b;
    }

    int Calculator::Multiply(int a, int b)
    {
        return a * b;
    }

    int Calculator::Fibonacci(int n)
    {
        if (n <= 0)
            return 0;
        if (n == 1)
            return 1;
        int prev = Fibonacci(n - 1);
        int prevPrev = Fibonacci(n - 2);
        return prev + prevPrev;
    }

    int Calculator::SumRange(int start, int end)
    {
        int accumulator = 0;
        accumulator = AccumulateSum(start, end, accumulator);
        int doubled = accumulator * 2;
        int halved = doubled / 2;
        return halved;
    }

    int Calculator::AccumulateSum(int current, int end, int accumulator)
    {
        if (current > end)
            return accumulator;
        accumulator += current;
        return AccumulateSum(current + 1, end, accumulator);
    }

    int Calculator::FactorialOrThrow(int n)
    {
        if (n < 0)
            throw std::invalid_argument("n must be non-negative");
        if (n <= 1)
            return 1;
        int sub = FactorialOrThrow(n - 1);
        return n * sub;
    }

    int Calculator::CountPrimes(int limit)
    {
        int count = 0;
        for (int i = 2; i <= limit; i++)
        {
            if (IsPrime(i))
            {
                count++;
            }
        }
        return count;
    }

    bool Calculator::IsPrime(int n)
    {
        if (n < 2)
            return false;
        for (int i = 2; i * i <= n; i++)
        {
            if (n % i == 0)
                return false;
        }
        return true;
    }
}
