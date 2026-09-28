import argparse
import json
import time
import numpy as np
import pandas as pd
from scipy.optimize import milp, LinearConstraint, Bounds

def solve_milp_flow_problem(supplies, 
                            sink_config, 
                            rules, 
                            demand_proportions, 
                            min_sinks_per_type):
    """
    Solves the message flow problem as a Mixed-Integer Linear Program (MILP).
    
    This script serves as a standalone reference implementation of Algorithm 1 
    (DIFT-aware Load Balancing: MILP Formulation) from Section 4.4 of the paper.
    It enforces a minimum "spread" of flow to K sinks to satisfy the Availability Objective.
    """
    
    # === 1. PRE-PROCESSING ===
    message_types = sorted(supplies.keys())
    sinks = sorted(sink_config.keys())
    
    if set(sinks) != set(demand_proportions.keys()):
        print("Error: Sinks in 'sink_config' and 'demand_proportions' must match.")
        return None, None

    m = len(message_types)
    n = len(sinks)
    
    # Total variables: m*n flow (f_ij) + m*n binary (y_ij)
    num_flow_vars = m * n
    num_binary_vars = m * n
    num_total_vars = num_flow_vars + num_binary_vars

    # Helper functions to get variable indices in the flat 'x' vector
    def f_idx(i, j): return (i * n) + j
    def y_idx(i, j): return (i * n + j) + num_flow_vars

    print(f"--- Problem Setup (MILP) ---")
    print(f"Sources (m): {m} ({message_types})")
    print(f"Sinks (n): {n} ({sinks})")
    print(f"Total variables: {num_total_vars}")
    print(f"Availability Target (c): {min_sinks_per_type}\n")

    # === 2. DEFINE CONSTANTS ===
    total_supply = sum(supplies.values())
    M = total_supply  # "Big M" constant
    epsilon = 0.1     # "Small epsilon" for lower bound
    
    # Objective function: We want a feasible solution respecting the rules.
    c = np.zeros(num_total_vars)

    # === 3. BUILD CONSTRAINTS (A, lb, ub) ===
    
    # --- A. Equality Constraints (Supply & Demand) ---
    A_eq = np.zeros((m + n, num_total_vars))
    b_eq = np.zeros(m + n)

    # Supply Constraints (Input Conservation)
    for i in range(m):
        for j in range(n):
            A_eq[i, f_idx(i, j)] = 1
        b_eq[i] = supplies[message_types[i]]

    # Demand Constraints (Target Load Deviation)
    total_proportion = sum(demand_proportions.values())
    if total_proportion == 0:
        print("Error: Total demand proportion cannot be zero.")
        return None, None
        
    for j in range(n):
        for i in range(m):
            A_eq[m + j, f_idx(i, j)] = 1
        
        sink_name = sinks[j]
        proportion = demand_proportions[sink_name]
        b_eq[m + j] = (proportion / total_proportion) * total_supply
        
    # --- B. Inequality Constraints (Linking & Spread) ---
    num_ineq_rows = (2 * m * n) + m
    A_ub = np.zeros((num_ineq_rows, num_total_vars))
    b_ub = np.zeros(num_ineq_rows)
    
    current_row = 0
    
    # Link Indicator Binding Constraints
    for i in range(m):
        for j in range(n):
            # 1. Upper-link: f_ij <= M * y_ij
            A_ub[current_row, f_idx(i, j)] = 1
            A_ub[current_row, y_idx(i, j)] = -M
            b_ub[current_row] = 0
            current_row += 1
            
            # 2. Lower-link: f_ij >= epsilon * y_ij
            A_ub[current_row, f_idx(i, j)] = -1
            A_ub[current_row, y_idx(i, j)] = epsilon
            b_ub[current_row] = 0
            current_row += 1

    # Redundancy Constraints
    # sum(y_ij for j in n) >= c  =>  -sum(y_ij for j in n) <= -c
    for i in range(m):
        for j in range(n):
            A_ub[current_row, y_idx(i, j)] = -1
        b_ub[current_row] = -min_sinks_per_type
        current_row += 1

    # --- C. Combine all constraints for MILP solver ---
    A = np.vstack((A_eq, A_ub))
    lb = np.concatenate((b_eq, -np.inf * np.ones_like(b_ub)))
    ub = np.concatenate((b_eq, b_ub))
    
    all_constraints = LinearConstraint(A, lb, ub)

    # === 4. BUILD BOUNDS & INTEGRALITY ===
    lb_bounds = [] 
    ub_bounds = [] 
    integrality = np.zeros(num_total_vars)

    # DIFT Flow Rules Mapping
    allowed_paths = set()
    for msg_name, allowed_types in rules.items():
        i = message_types.index(msg_name)
        for sink_name, sink_type in sink_config.items():
            j = sinks.index(sink_name)
            if sink_type in allowed_types:
                allowed_paths.add((i, j))

    # Bounds for flow variables (f_ij) enforcing DIFT constraints
    for i in range(m):
        for j in range(n):
            lb_bounds.append(0)
            if (i, j) in allowed_paths:
                ub_bounds.append(total_supply) # Allowed
            else:
                ub_bounds.append(0) # Forbidden
    
    # Bounds for binary variables (y_ij)
    for i in range(m):
        for j in range(n):
            integrality[y_idx(i, j)] = 1 # Mark as integer
            lb_bounds.append(0)
            if (i, j) in allowed_paths:
                ub_bounds.append(1) # Allowed: can be 0 or 1
            else:
                ub_bounds.append(0) # Forbidden: MUST be 0
    
    all_bounds = Bounds(lb=lb_bounds, ub=ub_bounds)
    
    # === 5. SOLVE THE PROBLEM ===
    print("--- Solving Mixed-Integer Linear Program ---")
    
    result = milp(c=c, 
                  constraints=all_constraints,
                  bounds=all_bounds,
                  integrality=integrality)

    # === 6. DISPLAY RESULTS ===
    if result.status == 0:
        print("✅ Solution found!")
        
        flow_solution = result.x[:num_flow_vars]
        flow_matrix = flow_solution.reshape((m, n))
        df = pd.DataFrame(flow_matrix, index=message_types, columns=sinks)
        
        binary_solution = result.x[num_flow_vars:]
        binary_matrix = binary_solution.reshape((m, n))
        df_bin = pd.DataFrame(binary_matrix, index=message_types, columns=sinks)
        
        print("\nFlow Matrix (f_ij):")
        print(df.round(6))
        
        return df, df_bin
    else:
        print("❌ No solution found.")
        print(f"Solver Message: {result.message} (Status code: {result.status})")
        return None, None

def main():
    parser = argparse.ArgumentParser(description="DIFT-aware MILP Load Balancer Reference Implementation")
    parser.add_argument('--config', type=str, help="Path to JSON configuration file (optional). If not provided, a default example will be used.")
    parser.add_argument('--redundancy', type=int, default=2, help="Availability Target (c) from Alg. 1: minimum active routes per label")
    
    args = parser.parse_args()

    if args.config:
        try:
            with open(args.config, 'r') as f:
                data = json.load(f)
            supplies = data.get('supplies')
            sinks = data.get('sinks')
            rules = data.get('rules')
            demand = data.get('demand_proportions')
        except Exception as e:
            print(f"Error reading config: {e}")
            return
    else:
        # Default example mirroring the structure in the paper
        supplies = {'W': 1, 'X': 2, 'Y': 2, 'Z': 1}
        sinks = {'A': 'W', 'B': 'Z', 'C': 'Z', 'D': 'Z', 'E': 'Z', 'F': 'Y', 'G': 'Y', 'H': 'Y', 'I': 'X', 'J': 'X'}
        rules = {'X': ['X', 'Z'], 'Y': ['Y', 'Z'], 'Z': ['Z'], 'W': ['W', 'X', 'Y', 'Z']}
        demand = {k: 1 for k in sinks.keys()}

    start_time = time.perf_counter_ns()
    solve_milp_flow_problem(
        supplies, 
        sinks, 
        rules, 
        demand,
        min_sinks_per_type=args.redundancy
    )
    end_time = time.perf_counter_ns()
    
    print("\n" + "="*40)
    print(f"Execution time: {(end_time - start_time) / 1e9:.6f} seconds")

if __name__ == "__main__":
    main()
