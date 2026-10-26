using System;

namespace DeltaV
{
    internal enum QpSolveStatus { NotRun, Optimal, FeasibleApproximate, IterationLimit, NumericalFailure, InvalidInput }

    /// <summary>
    /// Strictly convex dense QP: min .5 x'Px + q'x, l &lt;= Ax &lt;= u.
    /// Solves the nonnegative dual by adding/removing active bounds. Workspaces belong to
    /// this instance; iteration limits and tie breaking are deterministic, never wall-clock based.
    /// </summary>
    internal sealed class DenseActiveSetQpSolver
    {
        internal int IterationsUsed { get; private set; }
        internal QpSolveStatus Status { get; private set; }
        internal double RelativeGap { get; private set; }
        private int n, m, sides, activeCount;
        private double[] factor, free, work, inverse, scaledA, rowScale;
        private double[] h, v, y, bounds, signs, subFactor, subRhs, candidate;
        private double[] feasible, trial, feasibleTrial;
        private int[] rows, active;
        private bool[] inverseReady, columnReady, inside;
        private double[] p, q, a, lower, upper, tolerances;

        public bool Solve(int variables, int constraints, double[] objective, double[] linear,
            double[] matrix, double[] lo, double[] hi, int limit, double[] solution,
            out double primalResidual, out double dualResidual,
            double gapTolerance = 0, double[] rowTolerances = null)
        {
            IterationsUsed = 0;
            Status = QpSolveStatus.InvalidInput;
            RelativeGap = double.PositiveInfinity;
            primalResidual = dualResidual = double.PositiveInfinity;
            if (variables <= 0 || constraints < 0 || limit <= 0
                || !Fits(objective, variables * variables) || !Fits(linear, variables)
                || !Fits(matrix, constraints * variables) || !Fits(lo, constraints)
                || !Fits(hi, constraints) || !Fits(solution, variables)
                || (rowTolerances != null && !Fits(rowTolerances, constraints))
                || !Finite(gapTolerance) || gapTolerance < 0) return false;
            n = variables; m = constraints;
            p = objective; q = linear; a = matrix; lower = lo; upper = hi; tolerances = rowTolerances;
            EnsureCapacity();
            if (!AllFinite(p, n * n) || !AllFinite(q, n) || !AllFinite(a, m * n)) return false;
            sides = activeCount = 0;
            Array.Clear(inverseReady, 0, m);
            for (int row = 0; row < m; row++)
            {
                if (double.IsNaN(lo[row]) || double.IsNaN(hi[row]) || lo[row] > hi[row]
                    || !Finite(Tolerance(row)) || Tolerance(row) <= 0) return false;
                double scale = 0;
                for (int j = 0; j < n; j++) scale = Math.Max(scale, Math.Abs(a[row * n + j]));
                if (scale == 0) scale = 1;
                rowScale[row] = scale;
                for (int j = 0; j < n; j++) scaledA[row * n + j] = a[row * n + j] / scale;
                // MPC uses +/-1e30 as infinity. Normalize finite bounds after classifying them.
                if (hi[row] < 1e25) AddSide(row, 1, hi[row] / scale);
                if (lo[row] > -1e25) AddSide(row, -1, -lo[row] / scale);
            }
            EnsureDualCapacity();
            Array.Clear(y, 0, sides);
            Array.Clear(inside, 0, sides);
            Array.Clear(columnReady, 0, sides);
            Status = QpSolveStatus.NumericalFailure;
            Array.Copy(p, factor, n * n);
            // The MPC input cost makes P positive definite. Do not silently change its objective.
            if (!Cholesky(factor, n)) return false;
            for (int j = 0; j < n; j++) free[j] = -q[j];
            Backsolve(factor, free, free, n);
            if (!AllFinite(free, n)) return false;
            for (int i = 0; i < sides; i++) v[i] = signs[i] * RowDot(scaledA, rows[i], free) - bounds[i];

            bool haveFeasible = false;
            double bestCost = double.PositiveInfinity;
            if (gapTolerance > 0)
            {
                Array.Copy(solution, feasible, n);
                haveFeasible = Feasible(feasible, true);
                if (!haveFeasible)
                {
                    Array.Clear(feasible, 0, n);
                    haveFeasible = Feasible(feasible, true);
                }
                if (haveFeasible) bestCost = Cost(feasible);
            }
            while (IterationsUsed < limit)
            {
                IterationsUsed++;
                int add = -1;
                double worst = 0;
                for (int i = 0; i < sides; i++)
                {
                    if (inside[i]) continue;
                    double gradient = -v[i];
                    for (int k = 0; k < activeCount; k++) gradient += h[i * sides + active[k]] * y[active[k]];
                    double threshold = Math.Min(1e-7, 0.1 * Tolerance(rows[i]) / rowScale[rows[i]]);
                    if (gradient < -threshold && (add < 0 || gradient < worst)) { worst = gradient; add = i; }
                }
                if (add < 0)
                {
                    Reconstruct(solution);
                    Residuals(solution, out primalResidual, out dualResidual, out bool stationary);
                    if (Feasible(solution, false) && stationary)
                    {
                        Status = QpSolveStatus.Optimal;
                        RelativeGap = Gap(solution);
                        return true;
                    }
                    return false;
                }
                if (gapTolerance > 0 && haveFeasible && (IterationsUsed & 15) == 1
                    && TryApproximate(gapTolerance, ref bestCost, solution))
                {
                    Residuals(solution, out primalResidual, out dualResidual, out _);
                    Status = QpSolveStatus.FeasibleApproximate;
                    return true;
                }
                if (!PrepareColumn(add)) return false;
                active[activeCount++] = add;
                inside[add] = true;
                bool settled = false;
                while (IterationsUsed < limit)
                {
                    IterationsUsed++;
                    for (int i = 0; i < activeCount; i++) subRhs[i] = v[active[i]];
                    // Only a singular Schur system gets regularization for redundant bounds. Acceptance checks
                    // feasibility, stationarity and complementarity against the original QP.
                    if (!FactorActive(false) && !FactorActive(true)) return false;
                    Backsolve(subFactor, subRhs, candidate, activeCount);
                    if (!AllFinite(candidate, activeCount)) return false;
                    bool positive = true;
                    for (int i = 0; i < activeCount; i++) if (candidate[i] <= 0) positive = false;
                    if (positive)
                    {
                        for (int i = 0; i < activeCount; i++) y[active[i]] = candidate[i];
                        settled = true;
                        break;
                    }
                    double alpha = 1;
                    for (int i = 0; i < activeCount; i++)
                        if (candidate[i] <= 0 && y[active[i]] > candidate[i])
                            alpha = Math.Min(alpha, y[active[i]] / (y[active[i]] - candidate[i]));
                    for (int i = 0; i < activeCount; i++) y[active[i]] += alpha * (candidate[i] - y[active[i]]);
                    for (int i = activeCount - 1; i >= 0; i--)
                    {
                        if (y[active[i]] > 1e-10) continue;
                        y[active[i]] = 0;
                        inside[active[i]] = false;
                        for (int j = i; j < activeCount - 1; j++) active[j] = active[j + 1];
                        activeCount--;
                    }
                    if (activeCount == 0) { settled = true; break; }
                }
                if (!settled) break;
            }
            Reconstruct(solution);
            Residuals(solution, out primalResidual, out dualResidual, out bool finalStationary);
            // The final permitted active-set update may itself have solved the problem.
            if (Feasible(solution, false) && finalStationary)
            {
                Status = QpSolveStatus.Optimal;
                RelativeGap = Gap(solution);
                return true;
            }
            if (gapTolerance > 0 && haveFeasible && TryApproximate(gapTolerance, ref bestCost, solution))
            {
                Residuals(solution, out primalResidual, out dualResidual, out _);
                Status = QpSolveStatus.FeasibleApproximate;
                return true;
            }
            Status = QpSolveStatus.IterationLimit;
            return false;
        }

        private void AddSide(int row, double sign, double bound)
        {
            rows[sides] = row; signs[sides] = sign; bounds[sides++] = bound;
        }

        private bool FactorActive(bool regularize)
        {
            for (int i = 0; i < activeCount; i++)
                for (int j = 0; j < activeCount; j++)
                {
                    double value = h[active[i] * sides + active[j]];
                    if (regularize && i == j) value += 1e-12 * Math.Max(1e-12, Math.Abs(value));
                    subFactor[i * activeCount + j] = value;
                }
            return Cholesky(subFactor, activeCount);
        }

        private bool PrepareColumn(int side)
        {
            if (columnReady[side]) return true;
            int row = rows[side];
            if (!inverseReady[row])
            {
                Array.Copy(scaledA, row * n, work, 0, n);
                Backsolve(factor, work, work, n);
                if (!AllFinite(work, n)) return false;
                Array.Copy(work, 0, inverse, row * n, n);
                inverseReady[row] = true;
            }
            for (int i = 0; i < sides; i++)
            {
                double value = 0;
                for (int j = 0; j < n; j++) value += scaledA[rows[i] * n + j] * inverse[row * n + j];
                h[i * sides + side] = h[side * sides + i] = signs[i] * signs[side] * value;
            }
            columnReady[side] = true;
            return true;
        }

        private void Reconstruct(double[] x)
        {
            Array.Copy(free, x, n);
            for (int k = 0; k < activeCount; k++)
            {
                int side = active[k];
                double multiplier = signs[side] * y[side];
                for (int j = 0; j < n; j++) x[j] -= inverse[rows[side] * n + j] * multiplier;
            }
        }

        private bool TryApproximate(double tolerance, ref double bestCost, double[] solution)
        {
            Reconstruct(trial);
            if (!AllFinite(trial, n)) return false;
            double alpha = 1;
            for (int row = 0; row < m; row++)
            {
                double start = RowDot(a, row, feasible);
                double delta = RowDot(a, row, trial) - start;
                if (delta > 0) alpha = Math.Min(alpha, (upper[row] - start) / delta);
                else if (delta < 0) alpha = Math.Min(alpha, (lower[row] - start) / delta);
            }
            alpha = Math.Max(0, Math.Min(1, alpha)) * (1 - 1e-9);
            for (int j = 0; j < n; j++) feasibleTrial[j] = feasible[j] + alpha * (trial[j] - feasible[j]);
            double cost = Cost(feasibleTrial);
            if (cost < bestCost && Feasible(feasibleTrial, true))
            {
                Array.Copy(feasibleTrial, feasible, n);
                bestCost = cost;
            }
            RelativeGap = Gap(feasible);
            if (!Finite(RelativeGap) || RelativeGap < 0 || RelativeGap > tolerance) return false;
            Array.Copy(feasible, solution, n);
            return true;
        }

        // A nonnegative dual vector supplies a lower bound even before primal KKT convergence.
        // Solve P again for the dual bound instead of assuming a reconstructed trial is stationary.
        private double Gap(double[] x)
        {
            Array.Copy(q, work, n);
            double boundTerm = 0;
            for (int k = 0; k < activeCount; k++)
            {
                int side = active[k];
                if (!Finite(y[side]) || y[side] < 0) return double.PositiveInfinity;
                boundTerm += bounds[side] * y[side];
                for (int j = 0; j < n; j++) work[j] += signs[side] * scaledA[rows[side] * n + j] * y[side];
            }
            Backsolve(factor, work, trial, n);
            double bound = -boundTerm;
            for (int j = 0; j < n; j++) bound -= 0.5 * work[j] * trial[j];
            double cost = Cost(x);
            double scale = Math.Max(1, Math.Max(Math.Abs(cost), Math.Abs(bound)));
            double gap = (cost - bound) / scale;
            return gap >= -1e-10 ? Math.Max(0, gap) : double.PositiveInfinity;
        }

        private bool Feasible(double[] x, bool strict)
        {
            if (!AllFinite(x, n)) return false;
            for (int row = 0; row < m; row++)
            {
                double value = RowDot(a, row, x);
                double tolerance = strict ? Math.Min(1e-9, Tolerance(row)) : Tolerance(row);
                if (!Finite(value) || value < lower[row] - tolerance || value > upper[row] + tolerance) return false;
            }
            return true;
        }

        private void Residuals(double[] x, out double primal, out double dual, out bool stationary)
        {
            primal = dual = 0;
            stationary = AllFinite(x, n);
            for (int row = 0; row < m; row++)
            {
                double value = RowDot(a, row, x);
                primal = Math.Max(primal, Math.Max(lower[row] - value, value - upper[row]));
            }
            Array.Clear(work, 0, n);
            for (int k = 0; k < activeCount; k++)
            {
                int side = active[k];
                double slack = signs[side] * RowDot(scaledA, rows[side], x) - bounds[side];
                if (Math.Abs(slack) * rowScale[rows[side]] > Tolerance(rows[side])) stationary = false;
                for (int j = 0; j < n; j++) work[j] += signs[side] * scaledA[rows[side] * n + j] * y[side];
            }
            for (int j = 0; j < n; j++)
            {
                double px = RowDot(p, j, x);
                double residual = Math.Abs(px + q[j] + work[j]);
                double scale = Math.Max(Math.Abs(px), Math.Max(Math.Abs(q[j]), Math.Abs(work[j])));
                if (!Finite(residual) || residual > 1e-4 + 1e-4 * scale) stationary = false;
                dual = Math.Max(dual, residual);
            }
        }

        private double Cost(double[] x)
        {
            double cost = 0;
            for (int j = 0; j < n; j++) cost += x[j] * (0.5 * RowDot(p, j, x) + q[j]);
            return cost;
        }
        private double RowDot(double[] matrix, int row, double[] x)
        {
            double value = 0;
            for (int j = 0; j < n; j++) value += matrix[row * n + j] * x[j];
            return value;
        }
        private double Tolerance(int row) => tolerances == null ? 1e-7 : tolerances[row];
        private static bool Fits(double[] values, int size) => values != null && values.Length >= size;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool AllFinite(double[] values, int size)
        {
            for (int i = 0; i < size; i++) if (!Finite(values[i])) return false;
            return true;
        }
        private static void Grow<T>(ref T[] values, int size)
        {
            if (values == null || values.Length < size) values = new T[size];
        }
        private void EnsureCapacity()
        {
            Grow(ref factor, n * n); Grow(ref free, n); Grow(ref work, n);
            Grow(ref inverse, m * n); Grow(ref scaledA, m * n); Grow(ref rowScale, m);
            Grow(ref inverseReady, m); Grow(ref rows, 2 * m); Grow(ref signs, 2 * m); Grow(ref bounds, 2 * m);
            Grow(ref feasible, n); Grow(ref trial, n); Grow(ref feasibleTrial, n);
        }
        private void EnsureDualCapacity()
        {
            Grow(ref h, sides * sides); Grow(ref subFactor, sides * sides);
            Grow(ref v, sides); Grow(ref y, sides); Grow(ref subRhs, sides); Grow(ref candidate, sides);
            Grow(ref active, sides); Grow(ref inside, sides); Grow(ref columnReady, sides);
        }
        private static bool Cholesky(double[] matrix, int size)
        {
            for (int i = 0; i < size; i++)
                for (int j = 0; j <= i; j++)
                {
                    double sum = matrix[i * size + j];
                    for (int k = 0; k < j; k++) sum -= matrix[i * size + k] * matrix[j * size + k];
                    if (i == j)
                    {
                        if (!(sum > 0) || !Finite(sum)) return false;
                        matrix[i * size + j] = Math.Sqrt(sum);
                    }
                    else matrix[i * size + j] = sum / matrix[j * size + j];
                }
            return true;
        }
        private static void Backsolve(double[] factor, double[] rhs, double[] result, int size)
        {
            for (int i = 0; i < size; i++)
            {
                double value = rhs[i];
                for (int j = 0; j < i; j++) value -= factor[i * size + j] * result[j];
                result[i] = value / factor[i * size + i];
            }
            for (int i = size - 1; i >= 0; i--)
            {
                double value = result[i];
                for (int j = i + 1; j < size; j++) value -= factor[j * size + i] * result[j];
                result[i] = value / factor[i * size + i];
            }
        }
    }
}
