"""Rebuild the two architecture figures with python -m pip install matplotlib."""
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
from matplotlib import pyplot as plt
from matplotlib.patches import FancyArrowPatch, FancyBboxPatch

OUTPUT = Path(__file__).resolve().parent
INK, MUTED, TEAL, PAPER = "#172e38", "#47606a", "#086861", "#faf9f5"


def canvas():
    fig = plt.figure(figsize=(13, 6), facecolor=PAPER)
    ax = fig.add_axes([0, 0, 1, 1], xlim=(0, 13), ylim=(0, 6), aspect="equal")
    ax.axis("off")
    return fig, ax


def box(ax, x, y, w, h, title, body, color="#e5f1ed"):
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle="round,pad=0.015,rounding_size=0.10",
                                facecolor=color, edgecolor="none"))
    ax.text(x + 0.20, y + h - 0.19, title, va="top", color=INK, weight="bold", fontsize=14)
    ax.text(x + 0.20, y + h - 0.56, body, va="top", color=MUTED, fontsize=12, linespacing=1.38)


def arrow(ax, start, end):
    ax.add_patch(FancyArrowPatch(start, end, arrowstyle="-|>", linewidth=1.8,
                                 mutation_scale=15, color=TEAL, shrinkA=2, shrinkB=2))


def save(fig, name):
    fig.savefig(OUTPUT / name, dpi=150, facecolor=PAPER)
    plt.close(fig)


def selection():
    fig, ax = canvas()
    ax.text(0.25, 5.64, "How Vision selects a playing policy", fontsize=25, weight="bold", color=INK)
    ax.text(.26, 5.22, "One capture produces one offer. Each candidate carries numeric handles for the target Vision observed.", color=MUTED, fontsize=13)
    box(ax,.25,3.05,3.4,1.65,"1   Pick targets",
        "Explore finds a reachable frontier.\nProgress routes toward the Atlas\nobjective on the platform graph.")
    box(ax,4.32,3.05,3.8,1.65,"2   Build the v2 offer",
        "Each route becomes a Traverse ID.\nAdd observed enemies, recovery,\nvisible prompts or a selected menu.")
    box(ax,8.79,3.05,3.95,1.65,"3   Gemma scores IDs",
        'Integer priorities from 1 to 100.\nAn empty scores object abstains.\nEarlier offer wins a tie.')
    arrow(ax,(3.65,3.87),(4.32,3.87))
    arrow(ax,(8.12,3.87),(8.79,3.87))
    arrow(ax,(10.77,3.05),(10.77,2.63))
    box(ax,8.79,.93,3.95,1.69,"4   Dispatch",
        "Recheck the room, waypoint, actor,\nrecovery state or cue. Run the highest\neligible score automatically.", "#faead7")
    ax.text(.26,2.45,"One possible reply",fontsize=15,color=TEAL,weight="bold")
    ax.text(.26,1.85,'{"scores":{"c0":85,"c1":40}}',family="monospace",fontsize=18,color=INK)
    ax.text(.26,.54,"A changed target expires its binding. Every new decision gets its own candidate IDs.",color=MUTED,fontsize=13)
    save(fig, "policy-selection.png")


def controls():
    fig, ax = canvas()
    ax.text(.25, 5.63, "How a selected policy controls the game", fontsize=25, weight="bold", color=INK)
    ax.text(.26, 5.18, "The selected control determines whether playback needs a movement observation or a visible interface cue.",
            color=MUTED, fontsize=13)
    box(ax, .25, 2.86, 4.0, 1.9, "Native movement",
        "Traverse feeds its bound hint into both\nroute channels. Fight and Lifeblood\nsteer toward one tracked actor.")
    box(ax, .25, .63, 4.0, 1.79, "Direct control",
        "Recover holds Focus while grounded\nin a clear opening. Inspect taps Up.\nContinue confirms the selected entry.", "#faead7")
    box(ax, 5.13, 1.76, 3.05, 1.8, "One control lease",
        "Apply buttons with a deadline.\nNeutralize or release on stale\ninput, focus loss or stop.")
    box(ax, 9.02, 1.76, 3.72, 1.8, "Observe the outcome",
        "Read red health after Focus. Check for\na stored passage after Inspect. Log\nthe chosen ID and the end reason.")
    arrow(ax, (4.25, 3.72), (5.13, 3.02))
    arrow(ax, (4.25, 1.49), (5.13, 2.29))
    arrow(ax, (8.18, 2.66), (9.02, 2.66))
    ax.text(5.15, .84, "The next goal uses a fresh observation.", color=TEAL, fontsize=15, weight="bold")
    save(fig, "policy-controls.png")


if __name__ == "__main__":
    selection()
    controls()
