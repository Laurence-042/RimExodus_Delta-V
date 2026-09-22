using System;

namespace DeltaV
{
    /// <summary>
    /// 小型稠密凸 QP 求解器：min 1/2 x'Px + q'x，约束 l &lt;= Ax &lt;= u。
    /// 使用固定次数 ADMM，避免控制结果依赖墙钟时间；工作区复用，且不引用游戏类型。
    /// </summary>
    internal sealed class DenseAdmmQpSolver
    {
        private const double Rho = 10.0;
        private const double Sigma = 1e-6;

        private double[] factor;
        private double[] rhs;
        private double[] z;
        private double[] zPrevious;
        private double[] dual;
        private double[] ax;
        private double[] atWork;
        private int capacityVariables;
        private int capacityConstraints;

        public bool Solve(int variables, int constraints, double[] p, double[] q,
            double[] a, double[] lower, double[] upper, int iterations,
            double[] solution, out double primalResidual, out double dualResidual)
        {
            primalResidual = double.PositiveInfinity;
            dualResidual = double.PositiveInfinity;
            if (variables <= 0 || constraints <= 0 || iterations <= 0)
            {
                return false;
            }
            EnsureCapacity(variables, constraints);
            BuildFactor(variables, constraints, p, a);
            if (!CholeskyInPlace(factor, variables))
            {
                return false;
            }

            // Warm start the projected quantity from the caller-owned primal solution.
            Multiply(a, solution, ax, constraints, variables);
            for (int row = 0; row < constraints; row++)
            {
                if (!IsFinite(solution[0]) || !IsFinite(ax[row]))
                {
                    Array.Clear(solution, 0, variables);
                    Array.Clear(dual, 0, constraints);
                    Multiply(a, solution, ax, constraints, variables);
                }
                z[row] = Clamp(ax[row] + dual[row], lower[row], upper[row]);
            }

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                for (int row = 0; row < constraints; row++)
                {
                    atWork[row] = z[row] - dual[row];
                }
                MultiplyTranspose(a, atWork, rhs, constraints, variables);
                for (int col = 0; col < variables; col++)
                {
                    rhs[col] = Rho * rhs[col] - q[col] + Sigma * solution[col];
                }
                SolveCholesky(factor, rhs, solution, variables);
                if (!AllFinite(solution, variables)) return false;

                Multiply(a, solution, ax, constraints, variables);
                Array.Copy(z, zPrevious, constraints);
                for (int row = 0; row < constraints; row++)
                {
                    z[row] = Clamp(ax[row] + dual[row], lower[row], upper[row]);
                    dual[row] += ax[row] - z[row];
                }
            }

            primalResidual = 0.0;
            for (int row = 0; row < constraints; row++)
            {
                primalResidual = Math.Max(primalResidual, Math.Abs(ax[row] - z[row]));
                atWork[row] = z[row] - zPrevious[row];
            }
            MultiplyTranspose(a, atWork, rhs, constraints, variables);
            dualResidual = 0.0;
            for (int col = 0; col < variables; col++)
            {
                dualResidual = Math.Max(dualResidual, Math.Abs(Rho * rhs[col]));
            }
            return IsFinite(primalResidual) && IsFinite(dualResidual);
        }

        public void ResetDual()
        {
            if (dual != null) Array.Clear(dual, 0, dual.Length);
        }

        private void EnsureCapacity(int variables, int constraints)
        {
            if (variables > capacityVariables)
            {
                capacityVariables = variables;
                factor = new double[variables * variables];
                rhs = new double[variables];
            }
            if (constraints > capacityConstraints)
            {
                capacityConstraints = constraints;
                z = new double[constraints];
                zPrevious = new double[constraints];
                dual = new double[constraints];
                ax = new double[constraints];
                atWork = new double[constraints];
            }
        }

        private void BuildFactor(int variables, int constraints, double[] p, double[] a)
        {
            Array.Clear(factor, 0, variables * variables);
            for (int i = 0; i < variables; i++)
            {
                int iRow = i * variables;
                for (int j = 0; j < variables; j++)
                {
                    double value = p[iRow + j];
                    for (int row = 0; row < constraints; row++)
                    {
                        value += Rho * a[row * variables + i] * a[row * variables + j];
                    }
                    factor[iRow + j] = value;
                }
                factor[iRow + i] += Sigma;
            }
        }

        private static bool CholeskyInPlace(double[] matrix, int n)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = matrix[i * n + j];
                    for (int k = 0; k < j; k++)
                    {
                        sum -= matrix[i * n + k] * matrix[j * n + k];
                    }
                    if (i == j)
                    {
                        if (!(sum > 1e-12) || !IsFinite(sum)) return false;
                        matrix[i * n + j] = Math.Sqrt(sum);
                    }
                    else
                    {
                        matrix[i * n + j] = sum / matrix[j * n + j];
                    }
                }
                for (int j = i + 1; j < n; j++) matrix[i * n + j] = 0.0;
            }
            return true;
        }

        private static void SolveCholesky(double[] lower, double[] b, double[] x, int n)
        {
            for (int i = 0; i < n; i++)
            {
                double sum = b[i];
                for (int j = 0; j < i; j++) sum -= lower[i * n + j] * x[j];
                x[i] = sum / lower[i * n + i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = x[i];
                for (int j = i + 1; j < n; j++) sum -= lower[j * n + i] * x[j];
                x[i] = sum / lower[i * n + i];
            }
        }

        private static void Multiply(double[] matrix, double[] vector, double[] result, int rows, int cols)
        {
            for (int row = 0; row < rows; row++)
            {
                double sum = 0.0;
                int start = row * cols;
                for (int col = 0; col < cols; col++) sum += matrix[start + col] * vector[col];
                result[row] = sum;
            }
        }

        private static void MultiplyTranspose(double[] matrix, double[] vector, double[] result, int rows, int cols)
        {
            for (int col = 0; col < cols; col++)
            {
                double sum = 0.0;
                for (int row = 0; row < rows; row++) sum += matrix[row * cols + col] * vector[row];
                result[col] = sum;
            }
        }

        private static bool AllFinite(double[] values, int count)
        {
            for (int i = 0; i < count; i++) if (!IsFinite(values[i])) return false;
            return true;
        }

        private static double Clamp(double value, double lower, double upper)
        {
            return value < lower ? lower : (value > upper ? upper : value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
