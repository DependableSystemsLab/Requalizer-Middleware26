import os
import sys
import pandas as pd

def process_violations(file_path):
    """
    Reads raw violations data and computes the violation percentage.
    Assumed CSV format: Application, Configuration, Interface, MessagesTotal, MessagesViolated
    """
    try:
        df = pd.read_csv(file_path)
    except Exception as e:
        print(f"Error reading {file_path}: {e}")
        return pd.DataFrame()

    # Calculate violation percentage
    # Adding a small epsilon to avoid division by zero if MessagesTotal is 0
    df['ViolationPct'] = (df['MessagesViolated'] / df['MessagesTotal'].replace(0, 1)) * 100
    
    # Pivot to get the layout: Rows=Application, Cols=Configuration+Interface
    pivot_df = df.pivot_table(
        index='Application',
        columns=['Configuration', 'Interface'],
        values='ViolationPct',
        aggfunc='mean'
    )
    
    return pivot_df

def main():
    if len(sys.argv) < 3:
        print("Usage: python generate-violations-table.py <exp1_stable_data.csv> <exp2_dynamic_data.csv>")
        sys.exit(1)

    stable_file = sys.argv[1]
    dynamic_file = sys.argv[2]

    print("Generating Table 3: DIFT Violations...\n")

    # Process both datasets
    stable_pivot = process_violations(stable_file)
    dynamic_pivot = process_violations(dynamic_file)

    if stable_pivot.empty or dynamic_pivot.empty:
        print("Data is missing or malformed. Returning empty table.")
        sys.exit(1)

    # Ensure consistent ordering
    apps_order = ['AAL', 'FD', 'SPG']
    configs_order = ['Baseline', 'L-DIFT', 'Requalizer']
    interfaces_order = ['Internal', 'External']

    # Reindex for exact ordering
    stable_pivot = stable_pivot.reindex(index=apps_order)
    dynamic_pivot = dynamic_pivot.reindex(index=apps_order)

    # Format the table string
    print("-" * 85)
    print(f"{'':<15} | {'Baseline':<20} | {'L-DIFT':<20} | {'Requalizer':<20}")
    print(f"{'':<15} | {'Int.':<9} {'Ext.':<10} | {'Int.':<9} {'Ext.':<10} | {'Int.':<9} {'Ext.':<10}")
    print("-" * 85)

    def print_section(section_name, pivot_data):
        for i, app in enumerate(apps_order):
            row_label = f"{section_name}" if i == 0 else ""
            row_app = f"{app:<5}"
            
            # Format percentages
            vals = []
            for cfg in configs_order:
                for iface in interfaces_order:
                    try:
                        val = pivot_data.loc[app, (cfg, iface)]
                        vals.append(f"{val:0.2f}%")
                    except KeyError:
                        vals.append("N/A")

            # Combine row
            row_str = f"{row_label:<10} {row_app} | {vals[0]:<9} {vals[1]:<10} | {vals[2]:<9} {vals[3]:<10} | {vals[4]:<9} {vals[5]:<10}"
            print(row_str)

    print_section("Exp. 1", stable_pivot)
    print("-" * 85)
    print_section("Exp. 2", dynamic_pivot)
    print("-" * 85)
    
    # Save to files under $REQUALIZER_OUTPUT_ROOT (the current directory if it's not set)
    output_dir = os.environ.get("REQUALIZER_OUTPUT_ROOT", ".")
    out_file = os.path.join(output_dir, "violations-table.txt")
    try:
        os.makedirs(output_dir, exist_ok=True)
        with open(out_file, "w") as f:
            f.write("Table 3: DIFT Violations (See console output for formatting)\n")
            # We can also save the dataframes as CSV for artifact preservation
            stable_pivot.to_csv(os.path.join(output_dir, "violations_stable_processed.csv"))
            dynamic_pivot.to_csv(os.path.join(output_dir, "violations_dynamic_processed.csv"))
        print(f"\nTable output saved and processed data written to {out_file}.")
    except Exception as e:
        print(f"Failed to write output file: {e}")

if __name__ == "__main__":
    main()
