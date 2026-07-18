#!/bin/bash
# Launch the PDF Editor GUI for testing

set -e

# Source common functions
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Initialize logging
init_logging "gui"

# Configuration
BUILD_FIRST=true
BUILD_CONFIG="Release"
WATCH_MODE=false
VERBOSE=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --debug)
            BUILD_CONFIG="Debug"
            shift
            ;;
        --release)
            BUILD_CONFIG="Release"
            shift
            ;;
        --no-build)
            BUILD_FIRST=false
            shift
            ;;
        --watch)
            WATCH_MODE=true
            shift
            ;;
        --verbose|-v)
            VERBOSE=true
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --debug       Run in Debug configuration"
            echo "  --release     Run in Release configuration (default)"
            echo "  --no-build    Skip building before running"
            echo "  --watch       Run with dotnet watch (auto-reload on changes)"
            echo "  --verbose,-v  Show detailed output"
            echo "  --help, -h    Show this help"
            echo ""
            echo "This script launches the PDF Editor GUI application."
            echo "Use --watch for development to auto-reload on code changes."
            echo ""
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
            exit 1
            ;;
    esac
done

log_section "PDF Editor - GUI Launcher"

log_info "Configuration: $BUILD_CONFIG"
if [ "$WATCH_MODE" = true ]; then
    log_info "Watch mode: Enabled (auto-reload on changes)"
fi
log ""

# Find .NET
find_dotnet || exit 1

cd "$PROJECT_ROOT"

# Check for display
check_display() {
    if [ -z "$DISPLAY" ] && [ -z "$WAYLAND_DISPLAY" ]; then
        log_warning "No display detected"
        log_warning "Make sure you have a display server running"
        log_warning "For X11: export DISPLAY=:0"
        log_warning "For WSL: Use VcXsrv or WSLg"
    else
        log_info "Display: ${DISPLAY:-$WAYLAND_DISPLAY}"
    fi
}

# Build if needed
if [ "$BUILD_FIRST" = true ]; then
    log_section "Building PDF Editor"

    if [ "$VERBOSE" = true ]; then
        run_cmd "Building..." $DOTNET_CMD build Excise.App/Excise.App.csproj -c "$BUILD_CONFIG"
    else
        log_info "Building..."
        $DOTNET_CMD build Excise.App/Excise.App.csproj -c "$BUILD_CONFIG" >> "$LOG_FILE" 2>&1
        if [ $? -eq 0 ]; then
            log_success "Build successful"
        else
            log_error "Build failed - check log for details"
            exit 1
        fi
    fi
fi

# Check display before launching
check_display

# Launch the application
log_section "Launching PDF Editor"

if [ "$WATCH_MODE" = true ]; then
    log_info "Starting in watch mode..."
    log_info "Press Ctrl+C to stop"
    log ""

    # Run with watch
    cd Excise.App
    $DOTNET_CMD watch run -c "$BUILD_CONFIG" 2>&1 | tee -a "$LOG_FILE"
else
    log_info "Starting application..."
    log ""

    # Run normally
    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD run --project Excise.App -c "$BUILD_CONFIG" --no-build 2>&1 | tee -a "$LOG_FILE"
    else
        $DOTNET_CMD run --project Excise.App -c "$BUILD_CONFIG" --no-build 2>&1 | tee -a "$LOG_FILE"
    fi
fi

# This will only be reached if the app exits
log ""
log_info "Application exited"

print_summary "gui" "success"
