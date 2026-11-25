import copy
import unittest

import make_round8_variants as variants


def rectangle(x0, y0, x1, y1):
    return [dict(x=x, y=y) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]


class Charge02SharedAccessTests(unittest.TestCase):
    def setUp(self):
        self.definition = dict(
            name="B07", unit="m",
            nodes=[dict(id=id, x=x, y=y, resourceIds=resources) for id, x, y, resources in (
                ("CAD2-J0631", 0, 0, ["X-CAD2-J0631"]), ("CAD2-J0651", 1.64, 0, []),
                ("CHARGE-01", 0, -1.6, ["CHARGE-01"]), ("CHARGE-02", 1.64, -1.63, ["CHARGE-02"]),
                ("DUMPER-001", 2.54, 0, ["DUMPER-001"]),
            )],
            edges=[
                dict(id="CAD2-E01859", **{"from": "CAD2-J0651", "to": "CHARGE-02"}, enabled=True),
                dict(id="SIM-C02-EXIT", **{"from": "CHARGE-02", "to": "CAD2-J0631"},
                     bidirectional=False, enabled=True,
                     resourceIds=["CHARGE-02", "SEG-SIM-C02-EXIT", "X-CAD2-J0631"]),
            ],
            resources=[
                dict(id="CHARGE-01", type="ChargingBay", capacity=1),
                dict(id="CHARGE-02", type="ChargingBay", capacity=1),
                dict(id="SEG-SIM-C02-EXIT", type="Intersection", capacity=1),
                dict(id="X-CAD2-J0631", type="Intersection", capacity=1),
                dict(id="DUMPER-001", type="Station", capacity=1, boundary=rectangle(1.14, -1.1, 3.9, 1.1)),
            ],
            areaAnnotations=[dict(id="CAD-TERMINAL-PLATFORM", boundary=rectangle(3.16, -.71, 6.34, .71))],
        )
        self.scheme = dict(name="old", configuration=dict(
            vehicles=[dict(id="AGV-03", radiusMeters=.49)],
            dumper=dict(resourceId="DUMPER-001", dumpingSite="DUMPER-001"),
            chargingPriorityTest=dict(enabled=True, sites=[
                dict(chargingSiteId="CHARGE-01", resourceId="CHARGE-01"),
                dict(chargingSiteId="CHARGE-02", resourceId="CHARGE-02"),
            ]),
        ))

    def test_overlap_shares_dumper_lease_only_during_travel(self):
        original = copy.deepcopy(self.definition)
        candidate, audit = variants.charge02_shared_access(self.definition, self.scheme)
        definition = candidate["configuration"]["mapDefinition"]
        direct, diagonal = definition["edges"]
        self.assertTrue(diagonal["bidirectional"])
        self.assertFalse(direct["enabled"])
        self.assertIn("DUMPER-001", diagonal["resourceIds"])
        self.assertIn("X-CAD2-J0631", diagonal["resourceIds"])
        dock = next(node for node in definition["nodes"] if node["id"] == "CHARGE-02")
        self.assertEqual(dock["resourceIds"], ["CHARGE-02"])
        self.assertEqual(audit["sharedDumperResource"], "DUMPER-001")
        self.assertEqual(self.definition, original)

    def test_clear_operation_area_needs_no_dumper_lease(self):
        self.definition["resources"][-1]["boundary"] = rectangle(2.2, -1.1, 3.9, 1.1)
        candidate, audit = variants.charge02_shared_access(self.definition, self.scheme)
        diagonal = candidate["configuration"]["mapDefinition"]["edges"][-1]
        self.assertNotIn("DUMPER-001", diagonal["resourceIds"])
        self.assertIsNone(audit["sharedDumperResource"])

    def test_refuses_missing_boundaries_and_physical_overlap(self):
        for boundary in ([], rectangle(.2, -.5, .9, .1)):
            with self.subTest(boundary=boundary):
                self.definition["areaAnnotations"][0]["boundary"] = boundary
                with self.assertRaises(ValueError):
                    variants.charge02_shared_access(self.definition, self.scheme)

    def test_refuses_when_parked_vehicle_still_occupies_dumper_area(self):
        self.definition["resources"][-1]["boundary"] = rectangle(1.14, -1.2, 3.9, 1.1)
        with self.assertRaisesRegex(ValueError, "停稳"):
            variants.charge02_shared_access(self.definition, self.scheme)

    def test_vertical_inlet_checks_geometry_and_keeps_diagonal_exit(self):
        self.definition["nodes"].append(dict(id="CAD2-J0652", x=1.64, y=2, resourceIds=[], allowTrafficStop=False))
        self.definition["nodes"][1]["allowTrafficStop"] = False
        self.definition["edges"][0]["resourceIds"] = []
        self.definition["edges"].append(dict(id="CAD2-E01834", bidirectional=True, resourceIds=[],
                                             **{"from": "CAD2-J0651", "to": "CAD2-J0652"}))
        candidate, audit = variants.charge02_shared_access(self.definition, self.scheme, vertical_inlet=True)
        definition = candidate["configuration"]["mapDefinition"]
        inlet, diagonal, trunk = definition["edges"]
        self.assertTrue(inlet["enabled"])
        self.assertFalse(inlet.get("bidirectional", False))
        self.assertTrue(diagonal["bidirectional"])
        self.assertIn("DUMPER-001", inlet["resourceIds"])
        self.assertIn("DUMPER-001", trunk["resourceIds"])
        self.assertAlmostEqual(audit["verticalInlet"]["distanceFromDumperMeters"], .9)

        definition["areaAnnotations"][0]["boundary"] = rectangle(1.7, .65, 2, 1.2)
        with self.assertRaisesRegex(ValueError, "固定平台"):
            variants.validate_charge02_vertical_inlet(definition, candidate["configuration"])
