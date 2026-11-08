import unittest

from build_full_map import apply_dual_lane_topology


class DualLaneGuardTests(unittest.TestCase):
    def test_reciprocal_centrelines_share_capacity_one_head_on_guard(self):
        definition = {
            "nodes": [
                {"id": "A", "type": "Intersection", "x": 0.0, "y": 0.0},
                {"id": "B", "type": "Intersection", "x": 5.0, "y": 0.0},
            ],
            "edges": [
                {"id": "EAST", "from": "A", "to": "B", "resourceIds": []},
                {"id": "WEST", "from": "B", "to": "A", "resourceIds": []},
            ],
            "resources": [],
        }

        result = apply_dual_lane_topology(definition)

        guard = next(resource for resource in definition["resources"]
                     if resource["id"].startswith("HEAD-ON-GUARD-"))
        self.assertEqual(1, guard["capacity"])
        self.assertEqual(
            {guard["id"]},
            {resource for edge in definition["edges"]
             for resource in edge["resourceIds"]
             if resource.startswith("HEAD-ON-GUARD-")},
        )
        self.assertEqual(1, result["headOnGuardCount"])
        self.assertEqual(1, result["lanePairCount"])
        self.assertEqual(2, result["directedLaneCount"])


if __name__ == "__main__":
    unittest.main()
