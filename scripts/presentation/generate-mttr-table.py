import os
import sys
import pandas as pd
import numpy as np

def main():
    if len(sys.argv) < 2:
        print("Usage: python generate-mttr-table.py <mttr_data.csv>")
        sys.exit(1)

    data_file = sys.argv[1]

    print("Generating Table 4: Mean Time To Recover (MTTR)...\n")

    try:
        # Assumed format: Application, Configuration, RunID, MTTR_ms
        df = pd.read_csv(data_file)
    except Exception as e:
        print(f"Error reading {data_file}: {e}")
        sys.exit(1)

    if df.empty:
        print("Data is empty.")
        sys.exit(1)

    # Calculate mean and standard deviation for MTTR across runs
    agg_df = df.groupby(['Application', 'Configuration'])['MTTR_ms'].agg(['mean', 'std']).reset_index()

    # Pivot the data to get Application as rows and Configuration as columns
    mean_pivot = agg_df.pivot(index='Application', columns='Configuration', values='mean')
    std_pivot = agg_df.pivot(index='Application', columns='Configuration', values='std')

    apps_order = ['AAL', 'FD', 'SPG']
    configs_order = ['Baseline', 'L-DIFT', 'Requalizer']

    # Ensure consistent ordering and handle missing data
    mean_pivot = mean_pivot.reindex(index=apps_order, columns=configs_order).fillna(0)
    std_pivot = std_pivot.reindex(index=apps_order, columns=configs_order).fillna(0)

    # Formatting Table 4
    print("-" * 65)
    print(f"{'':<10} | {'Baseline':<15} | {'L-DIFT':<15} | {'Requalizer':<15}")
    print("-" * 65)

    for app in apps_order:
        vals = []
        for cfg in configs_order:
            mean_val = mean_pivot.loc[app, cfg]
            std_val = std_pivot.loc[app, cfg]
            # Format as "Mean ± StdDev"
            vals.append(f"{int(round(mean_val))} ± {int(round(std_val))}")
        
        row_str = f"{app:<10} | {vals[0]:<15} | {vals[1]:<15} | {vals[2]:<15}"
        print(row_str)
        
    print("-" * 65)

    # Save to file under $REQUALIZER_OUTPUT_ROOT (the current directory if it's not set)
    output_dir = os.environ.get("REQUALIZER_OUTPUT_ROOT", ".")
    out_file = os.path.join(output_dir, "mttr-table.txt")
    try:
        os.makedirs(output_dir, exist_ok=True)
        with open(out_file, "w") as f:
            f.write("Table 4: Mean Time To Recover (See console output for formatting)\n")
        print(f"\nTable output saved to {out_file}.")
    except Exception as e:
        print(f"Failed to write output file: {e}")

if __name__ == "__main__":
    main()
