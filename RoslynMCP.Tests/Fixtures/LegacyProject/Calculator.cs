using System;

namespace LegacyProject
{
    /// <summary>
    /// A simple calculator class for testing legacy project support.
    /// </summary>
    public class Calculator
    {
        public int Add(int a, int b)
        {
            return a + b;
        }

        public int Subtract(int a, int b)
        {
            return a - b;
        }

        public double Divide(double numerator, double denominator)
        {
            if (denominator == 0)
                throw new DivideByZeroException("Cannot divide by zero.");
            return numerator / denominator;
        }

        public string Greet(string name)
        {
            return string.Format("Hello, {0}!", name);
        }
    }
}
