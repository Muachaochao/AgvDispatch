"""One accelerated business clock shared by the isolated controller and devices."""
import os
import time


class SimulationClock:
    def __init__(self, rate, origin_seconds):
        if not isinstance(rate, (int, float)) or isinstance(rate, bool) or not 1 <= rate <= 8:
            raise ValueError('Simulation rate must be between 1 and 8.')
        if not isinstance(origin_seconds, (int, float)) or origin_seconds <= 0:
            raise ValueError('Simulation origin must be a positive Unix timestamp.')
        self.rate = float(rate)
        self.origin_seconds = float(origin_seconds)

    def monotonic(self):
        # The origin is a shared wall-clock anchor. Each child process starts
        # later, so calculating from its own monotonic zero would make the
        # devices permanently stale relative to the controller and service.
        return max(0.0, time.time() - self.origin_seconds) * self.rate

    def time(self):
        return self.origin_seconds + self.monotonic()

    def business_delay(self, seconds):
        return max(0.0, float(seconds) / self.rate)

    @classmethod
    def from_environment(cls):
        raw_rate = os.environ.get('AGV_SIMULATION_RATE', '1')
        raw_origin = os.environ.get('AGV_SIMULATION_ORIGIN_UNIX_MS')
        try:
            rate = float(raw_rate)
            origin_seconds = (
                int(raw_origin) / 1000
                if raw_origin
                else time.time()
            )
        except ValueError as error:
            raise ValueError('Invalid simulation clock environment.') from error
        return cls(rate, origin_seconds)


CLOCK = SimulationClock.from_environment()


def configure(rate, origin_seconds):
    global CLOCK
    CLOCK = SimulationClock(rate, origin_seconds)
    return CLOCK


def monotonic():
    return CLOCK.monotonic()


def time_now():
    return CLOCK.time()


def business_delay(seconds):
    return CLOCK.business_delay(seconds)
