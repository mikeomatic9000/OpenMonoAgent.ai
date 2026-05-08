#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Shared logging and command-execution helpers
#
# Sourced by install.sh and install_prereqs.sh. Do not execute directly.
#
# Modes:
#   Default      — clean step-based progress messages
#   Verbose      — shows all command output (enable via OPENMONO_VERBOSE=1)
#
# All output (both modes) is appended to OPENMONO_LOG_FILE for post-mortem.
# ──────────────────────────────────────────────────────────────────────────────

# Colors (disabled when not a TTY)
if [ -t 1 ]; then
    RED=$'\033[0;31m'
    GREEN=$'\033[0;32m'
    YELLOW=$'\033[1;33m'
    BLUE=$'\033[38;2;163;255;102m'
    CYAN=$'\033[0;36m'
    DIM=$'\033[2m'
    BOLD=$'\033[1m'
    NC=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; BLUE=""; CYAN=""; DIM=""; BOLD=""; NC=""
fi

# ── Terminal cleanup on exit ─────────────────────────────────────────────────
# Ensures cursor is visible and terminal state is reset on exit or interrupt

_cleanup_terminal() {
    # Show cursor (in case it was hidden)
    printf '\033[?25h'
    # Reset all attributes
    printf '\033[0m'
    # Clear any partial line
    printf '\n'
}

# Set trap for common exit signals
trap _cleanup_terminal EXIT INT TERM HUP

OPENMONO_VERBOSE="${OPENMONO_VERBOSE:-0}"

# Log file: shared across install_prereqs.sh and install.sh within one setup run
if [ -z "${OPENMONO_LOG_FILE:-}" ]; then
    OPENMONO_LOG_DIR="${OPENMONO_LOG_DIR:-$HOME/.openmono/logs}"
    mkdir -p "$OPENMONO_LOG_DIR"
    OPENMONO_LOG_FILE="$OPENMONO_LOG_DIR/setup-$(date +%Y%m%d-%H%M%S).log"
    export OPENMONO_LOG_FILE
    : > "$OPENMONO_LOG_FILE"
fi

# ── Internal ────────────────────────────────────────────────────────────────

_log() {
    printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$OPENMONO_LOG_FILE"
}

# ── Public helpers ──────────────────────────────────────────────────────────

# banner — top-of-script title
banner() {
    local title="$1"
    local width=60
    local pad=$(( (width - ${#title} - 2) / 2 ))
    echo ""
    printf "${BOLD}${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 $width))"
    printf "${BOLD}${BLUE}%*s %s %*s${NC}\n" $pad "" "$title" $pad ""
    printf "${BOLD}${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 $width))"
    echo ""
    _log "=== $title ==="
}

# step — numbered section header, e.g. step 3 8 "Download model"
step() {
    local n="$1" total="$2"
    shift 2
    local msg="$*"
    echo ""
    printf "${BOLD}${BLUE}[%s/%s]${NC} ${BOLD}%s${NC}\n" "$n" "$total" "$msg"
    _log "STEP [$n/$total]: $msg"
}

info()  { printf "  ${BLUE}ℹ${NC}  %s\n" "$*"; _log "INFO: $*"; }
ok()    { printf "  ${GREEN}✓${NC}  %s\n" "$*"; _log "OK: $*"; }
warn()  { printf "  ${YELLOW}⚠${NC}  %s\n" "$*"; _log "WARN: $*"; }
err()   { printf "  ${RED}✗${NC}  %s\n" "$*" >&2; _log "ERROR: $*"; }

# detail — only shown in verbose mode, always logged
detail() {
    _log "DETAIL: $*"
    if [ "$OPENMONO_VERBOSE" = "1" ]; then
        printf "     ${DIM}%s${NC}\n" "$*"
    fi
}

# run — execute a command, log all output
#   Verbose: stream output live AND to log
#   Default: suppress output (still captured in log)
# Returns the command's exit code.
run() {
    _log "RUN: $*"
    if [ "$OPENMONO_VERBOSE" = "1" ]; then
        printf "     ${DIM}\$ %s${NC}\n" "$*"
        "$@" 2>&1 | tee -a "$OPENMONO_LOG_FILE"
        return "${PIPESTATUS[0]}"
    else
        "$@" >> "$OPENMONO_LOG_FILE" 2>&1
    fi
}

# run_live — always stream output to user AND log (for downloads, builds)
run_live() {
    _log "RUN_LIVE: $*"
    "$@" 2>&1 | tee -a "$OPENMONO_LOG_FILE"
    return "${PIPESTATUS[0]}"
}

# show_log_tail — print last N lines of the log (for error diagnostics)
show_log_tail() {
    local lines="${1:-20}"
    echo ""
    printf "${DIM}─── Last %s log lines ───${NC}\n" "$lines"
    tail -"$lines" "$OPENMONO_LOG_FILE" | sed "s/^/    /"
    printf "${DIM}─────────────────────────${NC}\n"
    echo ""
}

# die — print error, show log tail, exit
die() {
    err "$*"
    show_log_tail 30
    echo ""
    err "Full log: $OPENMONO_LOG_FILE"
    exit 1
}

# show_summary — print log-file pointer at the end of a run
show_log_location() {
    echo ""
    printf "${DIM}Full log: %s${NC}\n" "$OPENMONO_LOG_FILE"
}

# get_shell_rc_files — output rc file paths for the user's current shell
# Outputs one file per line
get_shell_rc_files() {
    local shell_name
    shell_name=$(basename "$SHELL")

    case "$shell_name" in
        zsh)
            echo "$HOME/.zshrc"
            echo "$HOME/.zprofile"
            ;;
        bash)
            echo "$HOME/.bash_profile"
            echo "$HOME/.bashrc"
            ;;
        fish)
            echo "$HOME/.config/fish/config.fish"
            ;;
        *)
            echo "$HOME/.bashrc"
            echo "$HOME/.bash_profile"
            echo "$HOME/.zshrc"
            ;;
    esac
}

# role_prompt — interactive role selection (used during setup if OPENMONO_ROLE not set)
# Sets $OPENMONO_ROLE to: full, inference, or agent
role_prompt() {
    if [[ -n "${OPENMONO_ROLE:-}" ]]; then
        return 0  # Already set, skip prompt
    fi

    echo ""
    echo "  What do you want to install on this machine?"
    echo ""
    echo "  1) Both — agent + inference server on one box (single-box mode)"
    echo "  2) Inference server only — GPU box that runs the model"
    echo "             (pair with a separate agent box via openmono tunnel)"
    echo "  3) Agent only — laptop/workstation that talks to a remote inference server"
    echo "             (dual-box mode; point at inference box with openmono config)"
    echo ""
    while true; do
        printf "  Enter 1, 2 or 3 [default: 1]: "
        read -r _role_choice
        _role_choice="${_role_choice:-1}"
        case "$_role_choice" in
            1) OPENMONO_ROLE=full      ; break ;;
            2) OPENMONO_ROLE=inference ; break ;;
            3) OPENMONO_ROLE=agent     ; break ;;
            *) echo "  Please enter 1, 2, or 3." ;;
        esac
    done
    export OPENMONO_ROLE
    echo ""
}
