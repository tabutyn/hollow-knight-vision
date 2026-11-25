"""Render the Gemma memory and policy-request diagrams with Matplotlib."""
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch

OUT = Path(__file__).resolve().parent
INK, TEAL, DIM, PAPER = "#172e38", "#086861", "#47606a", "#faf9f5"
MINT, SAND = "#e5f1ed", "#faead7"


def page(title, sub):
    fig, ax = plt.subplots(figsize=(14, 7), facecolor=PAPER)
    fig.subplots_adjust(left=0, right=1, bottom=0, top=1)
    ax.set(xlim=(0, 14), ylim=(0, 7), aspect="equal")
    ax.axis("off")
    ax.text(.32, 6.45, title, fontsize=25, weight="bold", color=INK)
    ax.text(.34, 5.99, sub, fontsize=14, color=DIM)
    return fig, ax


def panel(ax, x, y, w, h, title, lines, color=MINT):
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle="round,pad=.02,rounding_size=.09",
                                facecolor=color, edgecolor="none"))
    ax.text(x+.20, y+h-.23, title, va="top", fontsize=15, weight="bold", color=INK)
    ax.text(x+.20, y+h-.68, lines, va="top", fontsize=13, color=DIM, linespacing=1.45)


def arrow(ax, start, end):
    ax.add_patch(FancyArrowPatch(start, end, arrowstyle="-|>", color=TEAL, lw=2, mutation_scale=16))


def save(fig, name):
    fig.savefig(OUT / name, dpi=150)
    plt.close(fig)


def journal():
    fig, ax = page("How a reading changes quest memory",
                   "One notebook per playthrough. Each accepted reading keeps a stable source number.")
    panel(ax, .32, 3.10, 3.81, 2.17, "OCR notebook",
          "Append text, room and language.\nKeep the raw reading and its\ncorrected transcription.", MINT)
    panel(ax, 5.02, 3.10, 3.95, 2.17, "Gemma update call",
          "Read the Atlas goal, new excerpts\nand previous notes. Return quests,\ndestinations and unknowns.", SAND)
    panel(ax, 9.85, 3.10, 3.82, 2.17, "Saved quest notes",
          "Verify cited source numbers.\nSave the replacement notes and\nprocessed prefix together.", MINT)
    arrow(ax, (4.16, 4.20), (4.95, 4.20))
    arrow(ax, (9.00, 4.20), (9.80, 4.20))
    ax.text(.34, 2.33, "When the notebook changes", color=TEAL, weight="bold", fontsize=16)
    ax.text(.34, 1.78, "New text", color=INK, weight="bold", fontsize=14)
    ax.text(2.00, 1.78, "Resume at the first unseen excerpt. Long readings pass through in several pages.", color=DIM, fontsize=14)
    ax.text(.34, 1.20, "Correction", color=INK, weight="bold", fontsize=14)
    ax.text(2.00, 1.20, "A change inside the processed prefix starts a replay from the corrected notebook.", color=DIM, fontsize=14)
    ax.text(.34, .62, "Reopen", color=INK, weight="bold", fontsize=14)
    ax.text(2.00, .62, "Load the saved notes and verify the prefix digest before reusing them.", color=DIM, fontsize=14)
    save(fig, "gemma-quest-memory.png")


def selector():
    fig, ax = page("The running v2 policy request",
                   "Vision takes the first available frame, then samples every minute. One policy request runs at a time.")
    rows = [
        ("1", "messages[0].content", "The fixed Atlas objective and the instructions for scoring offered candidates."),
        ("2", "messages[1].content", "Notebook excerpts, cited quest notes, live state, visible OCR and the offer of candidate IDs."),
        ("3", "messages[1].images[0]", "One clean game screenshot encoded as a PNG."),
        ("4", "format", "The reply schema allows only IDs from this offer, each scored from 1 to 100."),
    ]
    for i, (num, title, detail) in enumerate(rows):
        y = 5.18 - .80*i
        ax.text(.38, y, num, fontsize=21, weight="bold", color=TEAL, va="center")
        ax.text(.91, y+.11, title, fontsize=16, weight="bold", color=INK, va="center")
        ax.text(.92, y-.24, detail, fontsize=12.7, color=DIM, va="center")
    panel(ax, .35, .74, 5.90, 1.62, "Gemma returns JSON",
          "One scores object. Each key is an offered ID.\nAn empty scores object abstains.", SAND)
    panel(ax, 7.20, .74, 6.44, 1.62, "Vision chooses at playback",
          "Check the fresh screen and receiver when the reply arrives.\nDispatch one eligible maneuver automatically.")
    arrow(ax, (6.32, 1.56), (7.10, 1.56))
    save(fig, "gemma-policy-request.png")


if __name__ == "__main__":
    journal()
    selector()
