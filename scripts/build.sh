#!/bin/bash
# Build excise projects
# Usage: ./build.sh [OPTIONS] [PROJECT...]
#
# Projects: editor, tests, demo, all (default: all)

set -e

# Source common functions
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Initialize logging
init_logging "build"

# Configuration
BUILD_CONFIG="Release"
BUILD_TARGETS=()
PUBLISH=false
RUNTIME=""
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
        --publish)
            PUBLISH=true
            shift
            ;;
        --runtime|-r)
            RUNTIME="$2"
            shift 2
            ;;
        --verbose|-v)
            VERBOSE=true
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS] [PROJECT...]"
            echo ""
            echo "Projects:"
            echo "  editor    Build main PDF Editor application"
            echo "  tests     Build test project"
            echo "  demo      Build demo project"
            echo "  all       Build all projects (default)"
            echo ""
            echo "Options:"
            echo "  --debug           Build in Debug configuration"
            echo "  --release         Build in Release configuration (default)"
            echo "  --publish         Create standalone executable"
            echo "  --runtime, -r     Target runtime (linux-x64, win-x64, osx-x64)"
            echo "  --verbose, -v     Show detailed build output"
            echo "  --help, -h        Show this help"
            echo ""
            echo "Examples:"
            echo "  $0                        # Build all in Release"
            echo "  $0 editor                 # Build only editor"
            echo "  $0 editor tests           # Build editor and tests"
            echo "  $0 --publish -r linux-x64 # Publish standalone Linux executable"
            echo ""
            exit 0
            ;;
        editor|tests|demo|all)
            BUILD_TARGETS+=("$1")
            shift
            ;;
        *)
            log_error "Unknown option: $1"
            log_error "Use --help for usage"
            exit 1
            ;;
    esac
done

# Default to all if no targets specified
if [ ${#BUILD_TARGETS[@]} -eq 0 ]; then
    BUILD_TARGETS=("all")
fi

log_section "PDF Editor - Build Script"

log_info "Configuration: $BUILD_CONFIG"
log_info "Targets: ${BUILD_TARGETS[*]}"
if [ "$PUBLISH" = true ]; then
    log_info "Publishing: Yes"
    if [ -n "$RUNTIME" ]; then
        log_info "Runtime: $RUNTIME"
    else
        log_info "Runtime: (auto-detect)"
    fi
fi
log ""

# Find .NET
find_dotnet || exit 1

cd "$PROJECT_ROOT"

# Build functions
build_editor() {
    log_section "Building Excise.App"

    if [ ! -d "Excise.App" ]; then
        log_error "Excise.App directory not found"
        return 1
    fi

    local build_args="-c $BUILD_CONFIG"
    if [ "$VERBOSE" = true ]; then
        build_args="$build_args -v detailed"
    fi

    run_cmd "Restoring packages..." $DOTNET_CMD restore Excise.App/Excise.App.csproj
    run_cmd "Building..." $DOTNET_CMD build Excise.App/Excise.App.csproj $build_args

    log_success "Excise.App built successfully"
}

build_tests() {
    log_section "Building Excise.App.Tests"

    if [ ! -d "Excise.App.Tests" ]; then
        log_error "Excise.App.Tests directory not found"
        return 1
    fi

    local build_args="-c $BUILD_CONFIG"
    if [ "$VERBOSE" = true ]; then
        build_args="$build_args -v detailed"
    fi

    run_cmd "Restoring packages..." $DOTNET_CMD restore Excise.App.Tests/Excise.App.Tests.csproj
    run_cmd "Building..." $DOTNET_CMD build Excise.App.Tests/Excise.App.Tests.csproj $build_args

    log_success "Excise.App.Tests built successfully"
}

build_demo() {
    log_section "Building Excise.App.Demo"

    if [ ! -d "Excise.App.Demo" ]; then
        log_warning "Excise.App.Demo directory not found, skipping"
        return 0
    fi

    local build_args="-c $BUILD_CONFIG"
    if [ "$VERBOSE" = true ]; then
        build_args="$build_args -v detailed"
    fi

    run_cmd "Restoring packages..." $DOTNET_CMD restore Excise.App.Demo/Excise.App.Demo.csproj
    run_cmd "Building..." $DOTNET_CMD build Excise.App.Demo/Excise.App.Demo.csproj $build_args

    log_success "Excise.App.Demo built successfully"
}

publish_editor() {
    log_section "Publishing Excise.App"

    # Determine runtime
    local runtime="$RUNTIME"
    if [ -z "$runtime" ]; then
        case "$(uname -s)" in
            Linux*)  runtime="linux-x64" ;;
            Darwin*) runtime="osx-x64" ;;
            MINGW*|CYGWIN*|MSYS*) runtime="win-x64" ;;
            *)       runtime="linux-x64" ;;
        esac
        log_info "Auto-detected runtime: $runtime"
    fi

    local publish_args="-c $BUILD_CONFIG -r $runtime --self-contained true -p:PublishSingleFile=true"

    run_cmd "Publishing..." $DOTNET_CMD publish Excise.App/Excise.App.csproj $publish_args

    local output_dir="Excise.App/bin/$BUILD_CONFIG/net8.0/$runtime/publish"
    log_success "Published to: $output_dir"

    if [ -d "$output_dir" ]; then
        log ""
        log_info "Published files:"
        ls -lh "$output_dir" | while read line; do
            log "  $line"
        done
    fi
}

# Main execution
main() {
    local build_all=false

    for target in "${BUILD_TARGETS[@]}"; do
        if [ "$target" = "all" ]; then
            build_all=true
            break
        fi
    done

    if [ "$build_all" = true ]; then
        build_editor
        build_tests
        build_demo
    else
        for target in "${BUILD_TARGETS[@]}"; do
            case $target in
                editor) build_editor ;;
                tests)  build_tests ;;
                demo)   build_demo ;;
            esac
        done
    fi

    # Publish if requested
    if [ "$PUBLISH" = true ]; then
        publish_editor
    fi

    print_summary "build" "success"

    log "To run the application:"
    log "  $DOTNET_CMD run --project Excise.App -c $BUILD_CONFIG"
    log ""
}

main "$@"
