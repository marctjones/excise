#!/bin/bash
# Common functions for all excise scripts
# Source this file in other scripts: source "$(dirname "$0")/common.sh"

# Colors for terminal output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Project root directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOGS_DIR="$PROJECT_ROOT/logs"

# Create logs directory if it doesn't exist
mkdir -p "$LOGS_DIR"

# Current log file (set by init_logging)
LOG_FILE=""

# Initialize logging for a script
# Usage: init_logging "script-name"
init_logging() {
    local script_name="$1"
    local timestamp=$(date +%Y%m%d_%H%M%S)
    LOG_FILE="$LOGS_DIR/${script_name}_${timestamp}.log"

    # Create log file with header
    echo "=========================================" > "$LOG_FILE"
    echo "Log started: $(date)" >> "$LOG_FILE"
    echo "Script: $script_name" >> "$LOG_FILE"
    echo "Working directory: $(pwd)" >> "$LOG_FILE"
    echo "=========================================" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"

    # Also create/update a symlink to latest log
    ln -sf "$LOG_FILE" "$LOGS_DIR/${script_name}_latest.log"

    log_info "Logging to: $LOG_FILE"
}

# Log message to both screen and file
# Usage: log "message"
log() {
    local message="$1"
    local timestamp=$(date +"%Y-%m-%d %H:%M:%S")

    # Print to screen
    echo "$message"

    # Print to log file if initialized
    if [ -n "$LOG_FILE" ]; then
        echo "[$timestamp] $message" >> "$LOG_FILE"
    fi
}

# Log with colors (info, success, warning, error)
log_info() {
    local message="$1"
    local timestamp=$(date +"%Y-%m-%d %H:%M:%S")
    echo -e "${BLUE}[INFO]${NC} $message"
    if [ -n "$LOG_FILE" ]; then
        echo "[$timestamp] [INFO] $message" >> "$LOG_FILE"
    fi
}

log_success() {
    local message="$1"
    local timestamp=$(date +"%Y-%m-%d %H:%M:%S")
    echo -e "${GREEN}[SUCCESS]${NC} $message"
    if [ -n "$LOG_FILE" ]; then
        echo "[$timestamp] [SUCCESS] $message" >> "$LOG_FILE"
    fi
}

log_warning() {
    local message="$1"
    local timestamp=$(date +"%Y-%m-%d %H:%M:%S")
    echo -e "${YELLOW}[WARNING]${NC} $message"
    if [ -n "$LOG_FILE" ]; then
        echo "[$timestamp] [WARNING] $message" >> "$LOG_FILE"
    fi
}

log_error() {
    local message="$1"
    local timestamp=$(date +"%Y-%m-%d %H:%M:%S")
    echo -e "${RED}[ERROR]${NC} $message" >&2
    if [ -n "$LOG_FILE" ]; then
        echo "[$timestamp] [ERROR] $message" >> "$LOG_FILE"
    fi
}

# Log section header
log_section() {
    local title="$1"
    log ""
    log "========================================="
    log "$title"
    log "========================================="
    log ""
}

# Check if a command exists
check_command() {
    local cmd="$1"
    if command -v "$cmd" &> /dev/null; then
        return 0
    else
        return 1
    fi
}

# Find .NET SDK
find_dotnet() {
    if check_command dotnet; then
        DOTNET_CMD="dotnet"
    elif [ -f "$HOME/.dotnet/dotnet" ]; then
        DOTNET_CMD="$HOME/.dotnet/dotnet"
        export PATH="$HOME/.dotnet:$PATH"
    else
        log_error ".NET SDK not found"
        log_error "Please run: ./scripts/setup-dev.sh"
        return 1
    fi

    log_info ".NET SDK found: $($DOTNET_CMD --version)"
    return 0
}

# Run a command and log output
# Usage: run_cmd "description" command args...
run_cmd() {
    local description="$1"
    shift

    log_info "$description"

    if [ -n "$LOG_FILE" ]; then
        # Run command and capture output to both screen and log
        "$@" 2>&1 | tee -a "$LOG_FILE"
        local exit_code=${PIPESTATUS[0]}
    else
        "$@"
        local exit_code=$?
    fi

    if [ $exit_code -ne 0 ]; then
        log_error "Command failed with exit code: $exit_code"
        return $exit_code
    fi

    return 0
}

# Print script completion summary
print_summary() {
    local script_name="$1"
    local status="$2"

    log ""
    log "========================================="
    if [ "$status" = "success" ]; then
        log_success "$script_name completed successfully"
    else
        log_error "$script_name failed"
    fi
    log "========================================="
    log ""
    log "Log file: $LOG_FILE"
    log "Latest log symlink: $LOGS_DIR/${script_name}_latest.log"
    log ""
}

# Export PROJECT_ROOT for use in other scripts
export PROJECT_ROOT
export LOGS_DIR
