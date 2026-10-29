"""Offline, log-calibrated cannon/target kinematics experiment.

Run with: python scripts/CombatManeuverSimulation/simulate.py

This isolates a single weapon's constant-velocity lead and the target's lateral
acceleration. It is not a replay of RimWorld/MPC or a prediction of duel win rate.
"""

from math import pi, sqrt


RANGE = 150.0  # Logged cannon launches cluster around 140-170 cells.
SPEED = 45.0
RADIUS = 13.9  # Logged target hull bounding circle; real cell shape can be narrower.
INITIAL_LATERAL_SPEED = -2.34
DRAG_HALF_LIFE = 8.0  # Documented default passive linear drag, in seconds.
TICK = 1.0 / 60.0


def intercept_time(distance: float, lateral_speed: float) -> float:
    return distance / sqrt(SPEED * SPEED - lateral_speed * lateral_speed)


FLIGHT = intercept_time(RANGE, INITIAL_LATERAL_SPEED)
BULLET_VX = RANGE / FLIGHT
BULLET_VY = INITIAL_LATERAL_SPEED


def swept_shot(acceleration):
    """Return circle hit, closest center distance, and drift at closest pass."""
    target_y = 0.0
    velocity_y = INITIAL_LATERAL_SPEED
    closest = float("inf")
    closest_drift = 0.0
    hit_time = None
    for tick in range(1, 301):
        t = tick * TICK
        a = acceleration(t - TICK / 2.0)
        target_y += velocity_y * TICK + 0.5 * a * TICK * TICK
        velocity_y = (velocity_y + a * TICK) * 2.0 ** (-TICK / DRAG_HALF_LIFE)
        dx = RANGE - BULLET_VX * t
        drift = target_y - INITIAL_LATERAL_SPEED * t
        dy = target_y - BULLET_VY * t
        separation = sqrt(dx * dx + dy * dy)
        if separation < closest:
            closest, closest_drift = separation, drift
        if hit_time is None and separation <= RADIUS:
            hit_time = t
    return hit_time, closest, closest_drift


def constant(accel: float, delay: float = 0.0):
    return lambda t: accel if t >= delay else 0.0


def alternating(accel: float, hold: float, phase: float):
    return lambda t: accel if int((t + phase) / hold) % 2 == 0 else -accel


def asymmetric_stroke(forward: float, reverse: float, forward_hold: float,
                      reverse_hold: float, phase: float):
    cycle = forward_hold + reverse_hold
    return lambda t: forward if (t + phase) % cycle < forward_hold else -reverse


def ramp_during_turn(start_accel: float, end_accel: float, turn_seconds: float):
    """Optimistic translation-while-turning bound; not a thruster allocation solve."""
    return lambda t: start_accel + (end_accel - start_accel) \
        * min(1.0, t / turn_seconds)


def result_label(result):
    hit_time, closest, drift = result
    return (f"hit at {hit_time:.2f}s" if hit_time is not None else "miss") \
        + f"; closest={closest:.1f}, drift={drift:.1f}"


print(f"Inputs: range={RANGE:g}, shell speed={SPEED:g}, radius={RADIUS:g}, "
      f"target lateral speed={INITIAL_LATERAL_SPEED:g}, center flight={FLIGHT:.2f}s, "
      f"drag half-life={DRAG_HALF_LIFE:g}s")
print("Single shot, lateral acceleration begins after the shown delay:")
for label, a, delay in [
    ("constant-velocity lead", 0.0, 0.0),
    ("logged candidate maximum", 2.53, 0.0),
    ("logged current-heading reserve", 3.55, 0.0),
    ("logged side-heading reserve", 5.11, 0.0),
    ("side reserve, 0.5s turn", 5.11, 0.5),
    ("side reserve, 1.0s turn", 5.11, 1.0),
    ("side reserve, 1.5s turn", 5.11, 1.5),
    ("side reserve, 1.83s turn", 5.11, 1.83),
]:
    print(f"  {label:32s}: {result_label(swept_shot(constant(a, delay)))}")
for label, a in (("logged maximum, reversal at 1.5s", 2.53),
                 ("side reserve, reversal at 1.5s", 5.11)):
    print(f"  {label:32s}: "
          f"{result_label(swept_shot(lambda t: a if t < 1.5 else -a))}")
print("  thrust during 1.83s turn (ideal): "
      + result_label(swept_shot(ramp_during_turn(3.55, 5.11, 1.83))))

angle_change = 35.0 * pi / 180.0
angular_accel = 0.728  # Logged available angular acceleration at the first plan.
print(f"35 degree turn at {angular_accel:g} rad/s^2: "
      f"{sqrt(2 * angle_change / angular_accel):.2f}s to reach angle with angular speed, "
      f"{2 * sqrt(angle_change / angular_accel):.2f}s to stop there (ideal bounds)")

print("Shot launch phase across an alternating lateral maneuver (60 phases):")
print("  Each shot resets range/velocity; reversal is instantaneous and thrust is "
      "independent of rotation. These are optimistic bounds, not duel hit rates.")
for a in (2.53, 3.55, 5.11):
    for hold in (3.0, 6.0, 10.0):
        phases = [hold * 2.0 * i / 60.0 for i in range(60)]
        hits = sum(swept_shot(alternating(a, hold, phase))[0] is not None
                   for phase in phases)
        print(f"  accel={a:4.2f}, reverse every {hold:4.1f}s: "
              f"{hits}/60 circle hits")
asymmetric_phases = [9.0 * i / 60.0 for i in range(60)]
asymmetric_hits = sum(swept_shot(asymmetric_stroke(5.11, 2.55, 3.0, 6.0,
                                                   phase))[0] is not None
                      for phase in asymmetric_phases)
print(f"  illustrative unequal thrust (+5.11 for 3s, -2.55 for 6s): "
      f"{asymmetric_hits}/60 circle hits")
