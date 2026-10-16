using System;

namespace DeltaV
{
    /// <summary>
    /// 小型稠密凸 QP 求解器：min 1/2 x'Px + q'x，约束 l &lt;= Ax &lt;= u。
    /// ADMM 按数值残差收敛、限制最大迭代数；结果不依赖墙钟，工作区复用，不引用游戏类型。
    /// </summary>
    internal sealed class DenseAdmmQpSolver
    {
        private double rho;
        private double relativePrimal;
        private double relativeDual;
        private const double Sigma = 1e-6;
        private const double AbsoluteTolerance = 1e-4;
        private const double RelativeTolerance = 1e-4;
        private int IterationsUsed { get; set; }

        private double[] factor;
        private double[] rhs;
        private double[] z;
        private double[] dual;
        private double[] ax;
        private double[] atWork;
        private int capacityVariables;
        private int capacityConstraints;
        private double[] polishFactor, polishInverseRows, polishSchur, polishRhs, polishX, polishMultipliers;
        private int[] polishRows;

        public bool Solve(int variables, int constraints, double[] p, double[] q,
            double[] a, double[] lower, double[] upper, int iterations,
            double[] solution, out double primalResidual, out double dualResidual)
        {
            primalResidual = double.PositiveInfinity;
            dualResidual = double.PositiveInfinity;
            IterationsUsed = 0;
            relativePrimal = relativeDual = double.PositiveInfinity;
            if (variables <= 0 || constraints <= 0 || iterations <= 0)
            {
                return false;
            }
            EnsureCapacity(variables, constraints);
            // 调用方只暖启动原变量；模型/时间网格已变化，旧对偶乘子不再对应当前 QP。
            Array.Clear(dual, 0, constraints);
            rho = 5.0;
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
                    rhs[col] = rho * rhs[col] - q[col] + Sigma * solution[col];
                }
                SolveCholesky(factor, rhs, solution, variables);
                if (!AllFinite(solution, variables)) return false;

                Multiply(a, solution, ax, constraints, variables);
                for (int row = 0; row < constraints; row++)
                {
                    z[row] = Clamp(ax[row] + dual[row], lower[row], upper[row]);
                    dual[row] += ax[row] - z[row];
                }
                IterationsUsed = iteration + 1;
                if ((IterationsUsed & 7) == 0 || IterationsUsed == iterations)
                {
                    if (Converged(variables, constraints, p, q, a, solution,
                        out primalResidual, out dualResidual)) return true;
                    if ((IterationsUsed == 32 || IterationsUsed == 128 || IterationsUsed == iterations)
                        && Polish(variables, constraints, p, q, a, lower, upper, solution,
                            out double polishedPrimal, out double polishedDual))
                    {
                        primalResidual = polishedPrimal;
                        dualResidual = polishedDual;
                        return true;
                    }
                    // 固定迭代节拍的残差平衡；不使用耗时来决定是否重分解。
                    if ((IterationsUsed & 31) == 0 && IterationsUsed < iterations)
                    {
                        double next = Math.Max(0.01, Math.Min(10000.0,
                            rho * Math.Sqrt(Math.Max(relativePrimal, 1e-12) / Math.Max(relativeDual, 1e-12))));
                        if (next > rho * 2.0 || next < rho * 0.5)
                        {
                            double ratio = rho / next;
                            for (int row = 0; row < constraints; row++) dual[row] *= ratio;
                            rho = next;
                            BuildFactor(variables, constraints, p, a);
                            if (!CholeskyInPlace(factor, variables)) return false;
                        }
                    }
                }
            }
            return false;
        }

        // ADMM 确定活跃约束后解 KKT 系统精修；只接受通过原问题残差和符号检查的结果。
        // 这仍是同一 QP 的求解步骤，不修改目标、约束或调用任何备用控制律。
        private bool Polish(int n, int m, double[] p, double[] q, double[] a,
            double[] lower, double[] upper, double[] solution, out double primal, out double stationarity)
        {
            primal = stationarity = double.PositiveInfinity;
            int count = 0;
            for (int row = 0; row < m; row++)
                if (Math.Abs(dual[row]) > 1e-7) polishRows[count++] = row;
            Array.Copy(p, polishFactor, n * n);
            for (int i = 0; i < n; i++) polishFactor[i * n + i] += 1e-9;
            if (!CholeskyInPlace(polishFactor, n)) return false;
            for (int i = 0; i < n; i++) polishX[i] = -q[i];
            SolveCholesky(polishFactor, polishX, polishX, n);
            for (int k = 0; k < count; k++)
            {
                int row = polishRows[k];
                Array.Copy(a, row * n, rhs, 0, n);
                SolveCholesky(polishFactor, rhs, rhs, n);
                Array.Copy(rhs, 0, polishInverseRows, k * n, n);
                double value = -(dual[row] < 0 ? lower[row] : upper[row]);
                for (int col = 0; col < n; col++) value += a[row * n + col] * polishX[col];
                polishRhs[k] = value;
            }
            for (int i = 0; i < count; i++)
            for (int j = 0; j < count; j++)
            {
                double value = i == j ? 1e-9 : 0.0;
                for (int col = 0; col < n; col++)
                    value += a[polishRows[i] * n + col] * polishInverseRows[j * n + col];
                polishSchur[i * count + j] = value;
            }
            if (count > 0)
            {
                if (!CholeskyInPlace(polishSchur, count)) return false;
                SolveCholesky(polishSchur, polishRhs, polishMultipliers, count);
            }
            for (int k = 0; k < count; k++)
            {
                int row = polishRows[k];
                // 不等式乘子的符号单独检查，不能乘以旧乘子后再比较容差：
                // 旧乘子很小时会掩盖精修解的错误符号。等式乘子无符号限制。
                if (lower[row] != upper[row]
                    && (dual[row] < 0 ? polishMultipliers[k] > AbsoluteTolerance
                        : polishMultipliers[k] < -AbsoluteTolerance)) return false;
                for (int col = 0; col < n; col++)
                    polishX[col] -= polishInverseRows[k * n + col] * polishMultipliers[k];
            }
            if (!AllFinite(polishX, n)) return false;
            double primalScale = 0.0, dualScale = 0.0;
            primal = stationarity = 0.0;
            for (int row = 0; row < m; row++)
            {
                double value = 0;
                for (int col = 0; col < n; col++) value += a[row * n + col] * polishX[col];
                primal = Math.Max(primal, Math.Max(lower[row] - value, value - upper[row]));
                primalScale = Math.Max(primalScale, Math.Abs(value));
            }
            for (int col = 0; col < n; col++)
            {
                double px = 0, aty = 0;
                for (int j = 0; j < n; j++) px += p[col * n + j] * polishX[j];
                for (int k = 0; k < count; k++) aty += a[polishRows[k] * n + col] * polishMultipliers[k];
                stationarity = Math.Max(stationarity, Math.Abs(px + q[col] + aty));
                dualScale = Math.Max(dualScale, Math.Max(Math.Abs(px), Math.Max(Math.Abs(q[col]), Math.Abs(aty))));
            }
            if (!IsFinite(primal) || !IsFinite(stationarity)
                || primal > AbsoluteTolerance + RelativeTolerance * primalScale
                || stationarity > AbsoluteTolerance + RelativeTolerance * dualScale) return false;
            relativePrimal = primal / (1.0 + primalScale);
            relativeDual = stationarity / (1.0 + dualScale);
            Array.Copy(polishX, solution, n);
            return true;
        }

        // 真正的 KKT 平稳性残差 Px+q+A'y；停止只依赖数值，不依赖墙钟预算。
        private bool Converged(int n, int m, double[] p, double[] q, double[] a,
            double[] x, out double primalResidual, out double dualResidual)
        {
            primalResidual = dualResidual = 0.0;
            double primalScale = 0.0, dualScale = 0.0;
            for (int row = 0; row < m; row++)
            {
                primalResidual = Math.Max(primalResidual, Math.Abs(ax[row] - z[row]));
                primalScale = Math.Max(primalScale, Math.Max(Math.Abs(ax[row]), Math.Abs(z[row])));
                atWork[row] = rho * dual[row];
            }
            MultiplyTranspose(a, atWork, rhs, m, n);
            for (int col = 0; col < n; col++)
            {
                double px = 0.0;
                for (int j = 0; j < n; j++) px += p[col * n + j] * x[j];
                dualResidual = Math.Max(dualResidual, Math.Abs(px + q[col] + rhs[col]));
                dualScale = Math.Max(dualScale, Math.Max(Math.Abs(px), Math.Max(Math.Abs(q[col]), Math.Abs(rhs[col]))));
            }
            relativePrimal = primalResidual / (1.0 + primalScale);
            relativeDual = dualResidual / (1.0 + dualScale);
            return IsFinite(primalResidual) && IsFinite(dualResidual)
                && primalResidual <= AbsoluteTolerance + RelativeTolerance * primalScale
                && dualResidual <= AbsoluteTolerance + RelativeTolerance * dualScale;
        }

        private void EnsureCapacity(int variables, int constraints)
        {
            if (variables > capacityVariables)
            {
                capacityVariables = variables;
                factor = new double[variables * variables];
                rhs = new double[variables];
                polishFactor = new double[variables * variables];
                polishX = new double[variables];
            }
            if (constraints > capacityConstraints)
            {
                capacityConstraints = constraints;
                z = new double[constraints];
                dual = new double[constraints];
                ax = new double[constraints];
                atWork = new double[constraints];
                polishRows = new int[constraints];
                polishRhs = new double[constraints];
                polishMultipliers = new double[constraints];
                polishSchur = new double[constraints * constraints];
            }
            if (polishInverseRows == null || polishInverseRows.Length < variables * constraints)
                polishInverseRows = new double[variables * constraints];
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
                        value += rho * a[row * variables + i] * a[row * variables + j];
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
