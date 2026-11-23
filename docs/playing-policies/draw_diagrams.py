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
    ax.text(0.26, 5.22, "Gemma scores options offered for a captured frame. Vision binds their controls and checks the current screen.",
            color=MUTED, fontsize=13)
    box(ax, .25, 3.05, 3.4, 1.65, "Goal and evidence",
        "Atlas exploration by default\nor a player objective\nFrame, Atlas, OCR and notebook")
    box(ax, 4.32, 3.05, 3.8, 1.65, "Rank offered options",
        "Use only offered IDs and points\nReject invalid IDs and duplicates\nAllow an empty ranking")
    box(ax, 8.79, 3.05, 3.95, 1.65, "Dispatch on reply",
        "Check freshness and context\nCheck cue, menu and controls\nChoose the highest eligible score")
    arrow(ax, (3.65, 3.87), (4.32, 3.87))
    arrow(ax, (8.12, 3.87), (8.79, 3.87))
    arrow(ax, (10.77, 3.05), (10.77, 2.63))
    box(ax, 8.79, .93, 3.95, 1.69, "Run one choice",
        "Example: Interact / Inspect at 85\nwhen INSPECT is visible. Otherwise\nExplore / native at 30 can win.", "#faead7")
    ax.text(.26, 2.45, "Starting points for the planner", fontsize=15, color=TEAL, weight="bold")
    ax.text(.26, 1.92, "Recover  95     Fight  85     Interact  80", fontsize=16, color=INK)
    ax.text(.26, 1.45, "Progress  65    Traverse  55    Explore  40", fontsize=16, color=INK)
    ax.text(.26, .54, "A stale reply or a reply with no eligible choice issues no new maneuver.",
            color=MUTED, fontsize=13)
    save(fig, "policy-selection.png")


def controls():
    fig, ax = canvas()
    ax.text(.25, 5.63, "How a selected policy controls the game", fontsize=25, weight="bold", color=INK)
    ax.text(.26, 5.18, "The selected control determines whether playback needs a movement observation or a visible interface cue.",
            color=MUTED, fontsize=13)
    box(ax, .25, 2.86, 4.0, 1.9, "Native movement",
        "Use the compact skill view for a starter.\nA compatible trained ability head can\nadd a button to a motor or starter.")
    box(ax, .25, .63, 4.0, 1.79, "Direct control",
        "Inspect, Focus, dash and menu actions\nuse timed button sequences. Inspect\nalso starts the passage reader.", "#faead7")
    box(ax, 5.13, 1.76, 3.05, 1.8, "One control lease",
        "Apply buttons with a deadline.\nNeutralize or release on stale\ninput, focus loss or stop.")
    box(ax, 9.02, 1.76, 3.72, 1.8, "Observe the outcome",
        "Score newly mapped cells or a\nstored passage when available.\nLog the selected choice and end reason.")
    arrow(ax, (4.25, 3.72), (5.13, 3.02))
    arrow(ax, (4.25, 1.49), (5.13, 2.29))
    arrow(ax, (8.18, 2.66), (9.02, 2.66))
    ax.text(5.15, .84, "The next goal uses a fresh observation.", color=TEAL, fontsize=15, weight="bold")
    save(fig, "policy-controls.png")


if __name__ == "__main__":
    selection()
    controls()
