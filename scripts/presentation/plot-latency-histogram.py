import os
import sys
import pandas as pd
import numpy as np
import seaborn as sns
import matplotlib
import matplotlib.pyplot as plt
import matplotlib.ticker as ticker
from matplotlib.lines import Line2D

matplotlib.rcParams['pdf.fonttype'] = 42
matplotlib.rcParams['ps.fonttype'] = 42

def plot_latency_distribution(df):
    """
    Generates and saves the faceted, histogram outline plot.
    """
    print("Generating plot...")
    
    # Set the style for seaborn
    sns.set_theme(style="whitegrid", palette="muted")

    # Create the FacetGrid using displot
    g = sns.displot(
        data=df,
        x="Latency (ms)",
        hue="Configuration",
        col="Application",
        kind="hist",
        element="step",
        fill=True,
        bins=100,
        log_scale=False,
        stat="percent",
        height=4,
        aspect=0.9,
        facet_kws={
            'sharex': False
        },
        hue_order=["Baseline", "L-DIFT", "Requalizer"],
        common_bins=False,
        common_norm=False,
        alpha=1.0
    )
 
    # --- Add Percentile Lines ---
    
    # Manually create the color map
    hue_order_list = ["Baseline", "L-DIFT", "Requalizer"]
    palette = sns.color_palette("muted", n_colors=len(hue_order_list))
    colors = dict(zip(hue_order_list, palette))
    
    # Iterate over each facet (axis) in the plot
    for ax in g.axes.flat:
        # Get the application name for this facet
        app_name = ax.get_title().split(" = ")[-1]

        # Get the data just for this facet
        facet_data = df[df["Application"] == app_name]["Latency (ms)"]
        
        if not facet_data.empty:
            # Set the x-axis limits to be centered around the median
            padding = round(facet_data.max() * 0.15)
            data_min = max(0, round(facet_data.quantile(0.01)) - padding)
            data_max = padding + round(facet_data.quantile(0.99))
            ax.set_xlim(data_min, data_max)

        # Get the top of the y-axis for this facet
        plot_top_y = ax.get_ylim()[1]
        
        # Define the bin range for this whole facet
        app_data = df[df["Application"] == app_name]["Latency (ms)"]
        if app_data.empty:
            continue
            
        bin_range = (app_data.min(), app_data.max())
        
        # Iterate over each configuration to plot its percentiles
        for config_name, color in colors.items():
            subset = df[
                (df["Application"] == app_name) & 
                (df["Configuration"] == config_name)
            ]["Latency (ms)"]
            
            if subset.empty:
                continue

            # Calculate percentiles
            p50, p95, p99 = np.percentile(subset, [50, 95, 99])

            # Calculate histogram data manually
            hist_counts, bin_edges = np.histogram(
                subset, 
                bins=100, 
                range=bin_range, 
                density=False
            )
            
            hist_sum = hist_counts.sum()
            if hist_sum == 0:
                continue
                
            hist_y_pct = (hist_counts / hist_sum) * 100

            # Find the bin index for each percentile
            p99_bin_index = np.digitize(p99, bin_edges) - 1
            max_bin_index = len(hist_y_pct) - 1
            p99_bin_index = min(max(p99_bin_index, 0), max_bin_index)

            # Get the y-value (height) of the bin
            y_at_p99 = hist_y_pct[p99_bin_index]
            
            print(app_name, p99)
            ax.axvline(p99, color=color, linestyle=':', linewidth=2.0, alpha=1.0)

        # Format x-ticks
        ax.xaxis.set_major_formatter(ticker.ScalarFormatter())

    # Create a custom legend for the percentile linestyles
    percentile_legend_elements = [
        Line2D([0], [0], color='gray', linestyle=':', label='p99 Latency')
    ]
    
    # Get the handles and labels from the default Seaborn legend
    handles = g.legend.legend_handles
    labels = [t.get_text() for t in g.legend.texts]

    # Add custom percentile line legend elements
    handles.extend(percentile_legend_elements)
    labels.extend([h.get_label() for h in percentile_legend_elements])
    
    # Remove the default legend
    g.legend.remove()

    # Create a new, combined legend on the figure
    g.fig.legend(
        handles=handles,
        labels=labels,
        loc="upper center",
        bbox_to_anchor=(0.5, 0.99),
        ncol=4, 
        title=None,
        frameon=False
    )

    # Set titles and labels
    g.set_axis_labels("Latency (ms)", "Percent (%)")
    g.set_titles("Application: {col_name}", weight="bold")
    
    # Save the figure under $REQUALIZER_OUTPUT_ROOT (the current directory if it's not set)
    output_dir = os.environ.get("REQUALIZER_OUTPUT_ROOT", ".")
    os.makedirs(output_dir, exist_ok=True)
    output_filename = os.path.join(output_dir, "latency-plot.pdf")
    
    # Adjust tight_layout
    g.fig.tight_layout(rect=[0, 0.03, 1, 0.92]) 
    
    plt.savefig(output_filename, dpi=300, bbox_inches="tight")
    
    print(f"\nPlot saved successfully as '{output_filename}'")
    
    # Display the plot
    plt.show()

def main():
    df = pd.read_csv(datafile)
    
    if df is not None:
        plot_latency_distribution(df)
    else:
        print("Error: DataFrame is empty. Cannot generate plot.")

if (len(sys.argv) < 2):
    print("Provide data file")
    print("e.g., python latency-plot.py data.csv")
    sys.exit()

datafile = sys.argv[1]

if __name__ == "__main__":
    main()