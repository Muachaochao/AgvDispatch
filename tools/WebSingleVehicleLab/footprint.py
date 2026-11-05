"""Oriented vehicle footprint checks shared by the twin and independent verdict."""
import math


def vehicle_extents(value, default_radius=0.6):
    """Return length and width, deriving a conservative square for legacy profiles."""
    if not isinstance(value, dict):
        raise ValueError('车辆包络配置无效。')
    length = value.get('LengthMeters', value.get('lengthMeters'))
    width = value.get('WidthMeters', value.get('widthMeters'))
    if length is None and width is None:
        radius = value.get('RadiusFromReportedReferenceMeters', value.get('radiusMeters', default_radius))
        length = width = 2 * float(radius)
    if length is None or width is None:
        raise ValueError('车辆长宽必须同时提供。')
    length, width = float(length), float(width)
    if not 0.1 <= width <= length <= 20:
        raise ValueError('车辆长宽须满足0.1≤宽≤长≤20米。')
    return length, width


def _corners(x, y, angle, length, width, margin=0):
    half_length, half_width = length / 2 + margin / 2, width / 2 + margin / 2
    cosine, sine = math.cos(angle), math.sin(angle)
    longitudinal = (cosine, sine)
    lateral = (-sine, cosine)
    result = []
    for long_sign, wide_sign in ((1, 1), (-1, 1), (-1, -1), (1, -1)):
        result.append((x + long_sign * half_length * longitudinal[0] + wide_sign * half_width * lateral[0],
                       y + long_sign * half_length * longitudinal[1] + wide_sign * half_width * lateral[1]))
    return result, (longitudinal, lateral)


def rectangles_overlap(first, second, margin=0):
    """Separating-axis overlap test with combined clearance between rectangles."""
    if margin < 0 or not math.isfinite(margin):
        raise ValueError('车辆包络安全余量无效。')
    a, a_axes = _corners(float(first['x']), float(first['y']), float(first.get('angle', 0)),
                         float(first['lengthMeters']), float(first['widthMeters']), margin)
    b, b_axes = _corners(float(second['x']), float(second['y']), float(second.get('angle', 0)),
                         float(second['lengthMeters']), float(second['widthMeters']), margin)
    for axis in (*a_axes, *b_axes):
        project_a = [point[0] * axis[0] + point[1] * axis[1] for point in a]
        project_b = [point[0] * axis[0] + point[1] * axis[1] for point in b]
        if max(project_a) < min(project_b) or max(project_b) < min(project_a):
            return False
    return True


def swept_rectangles_overlap(first_from, first_to, second_from, second_to, margin=0.05):
    """Sample short twin steps so a fast crossing cannot tunnel between frames."""
    distance = max(math.hypot(float(first_to['x']) - float(first_from['x']),
                              float(first_to['y']) - float(first_from['y'])),
                   math.hypot(float(second_to['x']) - float(second_from['x']),
                              float(second_to['y']) - float(second_from['y'])))
    steps = max(2, min(64, math.ceil(distance / 0.05) + 1))
    for index in range(steps + 1):
        ratio = index / steps
        first = dict(first_from)
        first.update(x=float(first_from['x']) + (float(first_to['x']) - float(first_from['x'])) * ratio,
                     y=float(first_from['y']) + (float(first_to['y']) - float(first_from['y'])) * ratio,
                     angle=float(first_to.get('angle', first_from.get('angle', 0))))
        second = dict(second_from)
        second.update(x=float(second_from['x']) + (float(second_to['x']) - float(second_from['x'])) * ratio,
                      y=float(second_from['y']) + (float(second_to['y']) - float(second_from['y'])) * ratio,
                      angle=float(second_to.get('angle', second_from.get('angle', 0))))
        if rectangles_overlap(first, second, margin):
            return True
    return False
