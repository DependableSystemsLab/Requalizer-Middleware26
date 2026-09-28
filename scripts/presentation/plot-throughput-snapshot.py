import os
import sys
import pandas as pd
import numpy as np
import seaborn as sns
import matplotlib
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D

matplotlib.rcParams['pdf.fonttype'] = 42
matplotlib.rcParams['ps.fonttype'] = 42

def plot_throughput_timeseries(df):
    """
    Plots the throughput time series data as a 1x3 faceted line plot.
    
    Args:
        df (pd.DataFrame): The DataFrame with throughput data.
    """
    
    print("Generating plot...")
    
    # Set the theme for the plots
    sns.set_theme(style="whitegrid")
    
    # Create a 1x3 grid of line plots, one for each 'Application'
    g = sns.relplot(
        data=df,
        x='Time (sec)',
        y='Throughput (msg/s)',
        col='Application',
        hue='Configuration',
        kind='line',
        height=4,
        aspect=1.0,
        linewidth=1.5,
        facet_kws={
            'sharey': False,
            'sharex': False
        },
        alpha=1.0
    )
    
    # Add p1 horizontal lines
    
    # Get the colors used for each configuration from the legend
    legend_handles = g.legend.legend_handles
    legend_labels = [t.get_text() for t in g.legend.texts]

    # Filter out the "Configuration" title if it's there
    config_title = legend_labels[0]
    if config_title.lower() == 'configuration':
        legend_handles = legend_handles[1:]
        legend_labels = legend_labels[1:]

    # Create a color map
    colors = {label: handle.get_color() for label, handle in zip(legend_labels, legend_handles)}

    # Iterate over each facet (axis) in the plot
    for ax in g.axes.flat:
        # Get the application name for this facet
        app_name = ax.get_title().split(" = ")[-1]

        ax.set_xlim(0, 100000)
        
        # Iterate over each configuration to calculate p1
        for config_name, color in colors.items():
            # Get the subset of data for this app and config
            subset = df[
                (df["Application"] == app_name) & 
                (df["Configuration"] == config_name)
            ]["Throughput (msg/s)"]
            
            if subset.empty:
                continue
                
            # Calculate the p1 percentile
            p1_throughput = np.percentile(subset, 1)

            print(p1_throughput)
            
            # Draw the horizontal line
            ax.axhline(
                y=p1_throughput,
                color=color,
                linestyle=':',  # Dotted line for p1
                linewidth=2.0,
                alpha=1.0
            )

    # Create the custom legend entry for the p1 line
    p1_legend_element = Line2D(
        [0], [0], 
        color='gray', 
        linestyle=':', 
        linewidth=2.0, 
        label='p1 Throughput'
    )
    
    # Add the p1 handle and label to our lists
    legend_handles.append(p1_legend_element)
    legend_labels.append(p1_legend_element.get_label())
    
    # Remove the original legend
    g.legend.remove()

    # Create a new, combined legend on the figure
    g.fig.legend(
        handles=legend_handles,
        labels=legend_labels,
        loc="upper center",
        bbox_to_anchor=(0.5, 0.99),
        ncol=len(legend_labels),
        title=None,
        frameon=False
    )
    
    # Set titles and labels
    g.set_titles("Application: {col_name}", weight='bold')
    g.set_axis_labels("Time (ms)", "Throughput (bytes/s)")
    
    # Adjust tight_layout
    g.fig.tight_layout(rect=[0, 0.03, 1, 0.92]) 
    
    # Save the plot under $REQUALIZER_OUTPUT_ROOT (the current directory if it's not set), and show it
    output_dir = os.environ.get("REQUALIZER_OUTPUT_ROOT", ".")
    os.makedirs(output_dir, exist_ok=True)
    output_filename = os.path.join(output_dir, "throughput-plot.pdf")
    plt.savefig(output_filename, dpi=300, bbox_inches="tight")
    print(f"\nPlot saved as '{output_filename}'")
    plt.show()

if (len(sys.argv) < 2):
    print("Provide data file")
    print("e.g., python throughput-plot.py data.csv")
    sys.exit()

datafile = sys.argv[1]

if __name__ == "__main__":
    df = pd.read_csv(datafile)

    plot_throughput_timeseries(df)