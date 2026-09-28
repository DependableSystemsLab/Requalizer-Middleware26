"""
Reference implementation for Section 4.3: Workload Placement.
Workload Placement Scheduler using Constraint Programming (Google OR-Tools).

Features:
1. Resource Constraints: Ensures CPU/RAM limits are respected (Bin Packing).
2. Tag Constraints: Filters hosts based on DIFT requirements.
3. Topology Constraints: Handles bandwidth requirements between services.
4. Soft Constraints: Minimizes total latency score using CP optimization.
5. Group Anti-Affinity: Ensures resilient deployments by spreading replicas.
"""

import argparse
import random
import time
from ortools.sat.python import cp_model
from collections import defaultdict

def solve_scheduler(hosts, bandwidth_matrix, latency_matrix, services, service_links):
    # =========================================================================
    # HELPER LOGIC (Python-side filtering)
    # =========================================================================
    def is_host_valid(h_id, h_data, s_data):
        # Check Static Attributes
        for attr, required_val in s_data.get('attr_constraints', {}).items():
            if h_data['attrs'].get(attr) != required_val:
                return False

        # Check Flexible Tags (e.g. DIFT labels)
        h_tags = h_data.get('tags', {})
        for rule in s_data.get('tag_constraints', []):
            key = rule['key']
            op = rule['op']
            
            if key not in h_tags:
                return False
            
            val = h_tags[key]
            if op == '==' and val != rule['value']: return False
            if op == '!=' and val == rule['value']: return False
            if op == 'in' and val not in rule['values']: return False

        return True

    # =========================================================================
    # MODEL BUILDING
    # =========================================================================
    model = cp_model.CpModel()

    placement = {} 
    is_on = {}     

    print("--- Step 1: Pre-computing Valid Domains ---")
    for s_id, s_data in services.items():
        valid_hosts = [h for h, data in hosts.items() if is_host_valid(h, data, s_data)]
        
        if not valid_hosts:
            print(f"CRITICAL ERROR: Service '{s_data['name']}' has no valid hosts!")
            return False

        placement[s_id] = model.NewIntVarFromDomain(
            cp_model.Domain.FromValues(valid_hosts), 
            f'place_{s_id}'
        )

        for h_id in hosts:
            is_on[(s_id, h_id)] = model.NewBoolVar(f'ison_{s_id}_{h_id}')
            model.Add(placement[s_id] == h_id).OnlyEnforceIf(is_on[(s_id, h_id)])
            model.Add(placement[s_id] != h_id).OnlyEnforceIf(is_on[(s_id, h_id)].Not())

    # --- Constraint A: Resource Capacity ---
    print("--- Step 2: Applying Resource Constraints ---")
    resource_types = ['cpu', 'ram', 'disk']
    for h_id, h_data in hosts.items():
        for r_type in resource_types:
            limit = h_data['resources'][r_type]
            model.Add(
                sum(services[s]['reqs'][r_type] * is_on[(s, h_id)] for s in services) <= limit
            )

    # --- Constraint B: Topology ---
    print("--- Step 3: Applying Topology Constraints (BW & Latency) ---")
    total_bw_violation = 0 
    total_latency_score = 0 

    bw_table = []
    lat_table = []
    for h1 in hosts:
        for h2 in hosts:
            bw_table.append([h1, h2, bandwidth_matrix[h1][h2]])
            lat_table.append([h1, h2, latency_matrix[h1][h2]])

    for link in service_links:
        src, dst = link['src'], link['dst']
        bw_req = link['bw']
        max_lat = link.get('max_lat', 9999) 

        actual_bw = model.NewIntVar(0, 10000, f'bw_{src}_{dst}')
        model.AddAllowedAssignments([placement[src], placement[dst], actual_bw], bw_table)
        
        bw_violation = model.NewIntVar(0, 10000, f'bw_viol_{src}_{dst}')
        model.Add(bw_violation >= bw_req - actual_bw)
        total_bw_violation += bw_violation

        actual_lat = model.NewIntVar(0, 1000, f'lat_{src}_{dst}')
        model.AddAllowedAssignments([placement[src], placement[dst], actual_lat], lat_table)
        model.Add(actual_lat <= max_lat)
        total_latency_score += actual_lat

    # --- Constraint C: Group Anti-Affinity ---
    print("--- Step 4: Applying Group Anti-Affinity Constraints ---")
    groups = defaultdict(list)
    for s_id, s_data in services.items():
        if 'group' in s_data:
            groups[s_data['group']].append(s_id)
            
    for group_name, member_ids in groups.items():
        if len(member_ids) > 1:
            model.AddAllDifferent([placement[s_id] for s_id in member_ids])

    # Objective
    model.Minimize(total_bw_violation + total_latency_score)

    # =========================================================================
    # SOLVER EXECUTION & OUTPUT
    # =========================================================================
    solver = cp_model.CpSolver()
    status = solver.Solve(model)

    if status in [cp_model.OPTIMAL, cp_model.FEASIBLE]:
        print(f"\n✅ SOLUTION FOUND (Objective Cost: {solver.ObjectiveValue()})")
        for s in services:
            h = solver.Value(placement[s])
            print(f"Service {services[s]['name']:<20} --> Host {h:<2} [DIFT: {hosts[h]['tags']['dift']}]")
        return True
    else:
        print("\n❌ NO SOLUTION FOUND.")
        return False

def get_base_infrastructure(app='AAL'):
    hosts = {
        0: {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'public' }},
        1: {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'public' }},
        2: {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'public' }},
        3: {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'internal' }},
        4: {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'internal' }},
        5: {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'internal' }},
    }
    
    if app == 'AAL':
        hosts[6] = {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'patient', 'camera': 'true' }}
        hosts[7] = {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'patient', 'wearable': 'true' }}
    else:
        hosts[6] = {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'client' }}
        hosts[7] = {'resources': {'cpu': 2, 'ram': 4000, 'disk': 20000}, 'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'}, 'tags': { 'dift': 'client' }}

    bandwidth_matrix = [
        [1000, 100, 100, 100, 100, 100, 100, 100],
        [100, 1000, 100, 100, 100, 100, 100, 100],
        [100, 100, 1000, 100, 100, 100, 100, 100],
        [100, 100, 100, 1000, 100, 100, 100, 100],
        [100, 100, 100, 100, 1000, 100, 100, 100],
        [100, 100, 100, 100, 100, 1000, 100, 100],
        [100, 100, 100, 100, 100, 100, 1000, 100],
        [100, 100, 100, 100, 100, 100, 100, 1000]
    ]

    latency_matrix = [
        [1, 10, 10, 10, 10, 10, 10, 10],
        [10, 1, 10, 10, 10, 10, 10, 10],
        [10, 10, 1, 10, 10, 10, 10, 10],
        [10, 10, 10, 1, 10, 10, 10, 10],
        [10, 10, 10, 10, 1, 10, 10, 10],
        [10, 10, 10, 10, 10, 1, 10, 10],
        [10, 10, 10, 10, 10, 10, 1, 10],
        [10, 10, 10, 10, 10, 10, 10, 1],
    ]
    return hosts, bandwidth_matrix, latency_matrix


def run_aal(aware=False):
    hosts, bw, lat = get_base_infrastructure('AAL')
    
    if aware:
        services = {
            0: {'name': 'VideoCamera', 'reqs': {'cpu': 1, 'ram': 500, 'disk': 0}, 'tag_constraints': [{'key': 'camera', 'op': 'in', 'values': ['true']}]},
            1: {'name': 'FallDetector', 'reqs': {'cpu': 1, 'ram': 2000, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'patient']}]},
            2: {'name': 'FallDetector (SB)', 'reqs': {'cpu': 1, 'ram': 2000, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'patient']}]},
            3: {'name': 'Wearable', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'wearable', 'op': 'in', 'values': ['true']}]},
            4: {'name': 'RecordManager', 'reqs': {'cpu': 1, 'ram': 200, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            5: {'name': 'AIDoctor', 'reqs': {'cpu': 1, 'ram': 1000, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            6: {'name': 'WebServer', 'reqs': {'cpu': 1, 'ram': 1000, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            7: {'name': 'Notifier 1', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            8: {'name': 'Notifier 2', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            9: {'name': 'Notifier 3', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['patient']}]},
            10: {'name': 'Notifier 4', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['public']}]}
        }
    else:
        services = {
            0: {'name': 'VideoCamera', 'reqs': {'cpu': 2, 'ram': 500, 'disk': 0}, 'tag_constraints': [{'key': 'camera', 'op': 'in', 'values': ['true']}]},
            1: {'name': 'FallDetector', 'reqs': {'cpu': 2, 'ram': 2000, 'disk': 0}, 'tag_constraints': []},
            2: {'name': 'FallDetector (SB)', 'reqs': {'cpu': 2, 'ram': 2000, 'disk': 0}, 'tag_constraints': []},
            3: {'name': 'Wearable', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'wearable', 'op': 'in', 'values': ['true']}]},
            4: {'name': 'RecordManager', 'reqs': {'cpu': 1, 'ram': 200, 'disk': 0}, 'tag_constraints': []},
            5: {'name': 'AIDoctor', 'reqs': {'cpu': 1, 'ram': 1000, 'disk': 0}, 'tag_constraints': []},
            6: {'name': 'WebServer', 'reqs': {'cpu': 1, 'ram': 1000, 'disk': 0}, 'tag_constraints': []},
            7: {'name': 'Notifier 1', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            8: {'name': 'Notifier 2', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            9: {'name': 'Notifier 3', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            10: {'name': 'Notifier 4', 'group': 'notifier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []}
        }

    service_links = [
        {'src': 0, 'dst': 1, 'bw': 100, 'max_lat': 10},
        {'src': 0, 'dst': 2, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 4, 'bw': 100, 'max_lat': 1000},
        {'src': 2, 'dst': 4, 'bw': 100, 'max_lat': 1000},
        {'src': 4, 'dst': 5, 'bw': 500, 'max_lat': 5}
    ]

    solve_scheduler(hosts, bw, lat, services, service_links)

def run_fd(aware=False):
    hosts, bw, lat = get_base_infrastructure('FD')

    if aware:
        services = {
            0: {'name': 'TransactionParser', 'reqs': {'cpu': 2, 'ram': 500, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client']}]},
            1: {'name': 'Predictor 1', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            2: {'name': 'Predictor 2', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            3: {'name': 'Predictor 3', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['client']}]},
            4: {'name': 'Predictor 4', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['public']}]}
        }
    else:
        services = {
            0: {'name': 'TransactionParser', 'reqs': {'cpu': 2, 'ram': 500, 'disk': 0}, 'tag_constraints': []},
            1: {'name': 'Predictor 1', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            2: {'name': 'Predictor 2', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            3: {'name': 'Predictor 3', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            4: {'name': 'Predictor 4', 'group': 'predictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []}
        }

    service_links = [
        {'src': 0, 'dst': 1, 'bw': 100, 'max_lat': 10},
        {'src': 0, 'dst': 2, 'bw': 100, 'max_lat': 10},
        {'src': 0, 'dst': 3, 'bw': 100, 'max_lat': 10},
        {'src': 0, 'dst': 4, 'bw': 100, 'max_lat': 10}
    ]

    solve_scheduler(hosts, bw, lat, services, service_links)

def run_sg(aware=False):
    hosts, bw, lat = get_base_infrastructure('SPG')

    if aware:
        services = {
            0: {'name': 'SmartPlug', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client', 'public']}]},
            1: {'name': 'SlidingWindow', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client']}]},
            2: {'name': 'HouseLoadPredictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client']}]},
            3: {'name': 'PlugLoadPredictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client']}]},
            4: {'name': 'PlugMedian 1', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            5: {'name': 'PlugMedian 2', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            6: {'name': 'PlugMedian 3', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['client']}]},
            7: {'name': 'PlugMedian 4', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['public']}]},
            8: {'name': 'GlobalMedian', 'group': 'globalmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            9: {'name': 'GlobalMedian (SB)', 'group': 'globalmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            10: {'name': 'OutlierDetector', 'group': 'outlier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]},
            11: {'name': 'OutlierDetector (SB)', 'group': 'outlier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal']}]}
        }
    else:
        services = {
            0: {'name': 'SmartPlug', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client', 'public']}]},
            1: {'name': 'SlidingWindow', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            2: {'name': 'HouseLoadPredictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            3: {'name': 'PlugLoadPredictor', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            4: {'name': 'PlugMedian 1', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            5: {'name': 'PlugMedian 2', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            6: {'name': 'PlugMedian 3', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            7: {'name': 'PlugMedian 4', 'group': 'plugmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            8: {'name': 'GlobalMedian', 'group': 'globalmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            9: {'name': 'GlobalMedian (SB)', 'group': 'globalmedian', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            10: {'name': 'OutlierDetector', 'group': 'outlier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []},
            11: {'name': 'OutlierDetector (SB)', 'group': 'outlier', 'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 'tag_constraints': []}
        }

    service_links = [
        {'src': 0, 'dst': 1, 'bw': 100, 'max_lat': 10},
        {'src': 0, 'dst': 2, 'bw': 100, 'max_lat': 10},
        {'src': 0, 'dst': 3, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 4, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 5, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 6, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 7, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 8, 'bw': 100, 'max_lat': 10},
        {'src': 1, 'dst': 9, 'bw': 100, 'max_lat': 10},
        {'src': 8, 'dst': 10, 'bw': 100, 'max_lat': 10},
        {'src': 8, 'dst': 11, 'bw': 100, 'max_lat': 10},
        {'src': 9, 'dst': 10, 'bw': 100, 'max_lat': 10},
        {'src': 9, 'dst': 11, 'bw': 100, 'max_lat': 10}
    ]

    solve_scheduler(hosts, bw, lat, services, service_links)

def generate_cluster_topology(host_count, tags):
    """Generates a homogeneous cluster with BW and latency matrices."""
    hosts = {}
    for i in range(host_count):
        hosts[i] = {
            'resources': {'cpu': 16, 'ram': 4000, 'disk': 20000},
            'attrs': {'kernel': '5.4', 'zone': 'us-east-1a'},
            'tags': {'dift': random.choice(tags)}
        }
        
    bandwidth_matrix = [[1000 if i == j else 100 for j in range(host_count)] for i in range(host_count)]
    latency_matrix = [[1 if i == j else 10 for j in range(host_count)] for i in range(host_count)]
    return hosts, bandwidth_matrix, latency_matrix

def run_scalability_test(host_count=16, parallel_count=10):
    tags = ['internal', 'client', 'public']
    hosts, bw, lat = generate_cluster_topology(host_count, tags)

    services = {
        0: {'name': 'TransactionParser', 'reqs': {'cpu': 2, 'ram': 500, 'disk': 0}, 'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': ['internal', 'client']}]}
    }
    
    service_links = []
    for i in range(1, parallel_count + 1):
        services[i] = {
            'name': f'Predictor {i}', 
            'group': 'predictor', 
            'reqs': {'cpu': 1, 'ram': 100, 'disk': 0}, 
            'tag_constraints': [{'key': 'dift', 'op': 'in', 'values': [random.choice(tags)]}]
        }
        service_links.append({'src': 0, 'dst': i, 'bw': 5, 'max_lat': 10})

    solve_scheduler(hosts, bw, lat, services, service_links)

def main():
    parser = argparse.ArgumentParser(description="DIFT-Aware Scheduler Reference Implementation")
    parser.add_argument('--app', choices=['AAL', 'FD', 'SPG', 'SCALE'], default='AAL', help="Application topology to simulate")
    parser.add_argument('--mode', choices=['aware', 'unaware'], default='aware', help="Enable or disable DIFT-awareness")
    
    # Options specifically for SCALE
    parser.add_argument('--hosts', type=int, default=16, help="(SCALE only) Number of physical hosts")
    parser.add_argument('--services', type=int, default=10, help="(SCALE only) Number of parallel services")
    parser.add_argument('--seed', type=int, default=42, help="Random seed")
    
    args = parser.parse_args()

    random.seed(args.seed)
    
    started = time.perf_counter()
    
    is_aware = (args.mode == 'aware')
    
    if args.app == 'AAL':
        print(f"--- Running Ambient Assisted Living (AAL) - Mode: {args.mode.upper()} ---")
        run_aal(is_aware)
    elif args.app == 'FD':
        print(f"--- Running Fraud Detection (FD) - Mode: {args.mode.upper()} ---")
        run_fd(is_aware)
    elif args.app == 'SPG':
        print(f"--- Running Smart Power Grid (SPG) - Mode: {args.mode.upper()} ---")
        run_sg(is_aware)
    elif args.app == 'SCALE':
        print(f"--- Running Scalability Test ({args.hosts} hosts, {args.services} parallel services) ---")
        run_scalability_test(args.hosts, args.services)

    elapsed = time.perf_counter() - started
    print(f"\nExecution time: {elapsed:.4f} seconds")

if __name__ == '__main__':
    main()
