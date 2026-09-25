/* Loads the published library with dlopen and calls the stub exports. */
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>

int main(int argc, char** argv) {
    if (argc < 2) { fprintf(stderr, "usage: smoke <library>\n"); return 2; }
    void* h = dlopen(argv[1], RTLD_NOW);
    if (!h) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
    const char* (*version)(void) = (const char* (*)(void))dlsym(h, "excise_version");
    int (*abi)(void) = (int (*)(void))dlsym(h, "excise_abi_version");
    const char* (*last)(void) = (const char* (*)(void))dlsym(h, "excise_last_error");
    if (!version || !abi || !last) { fprintf(stderr, "missing export\n"); return 1; }
    printf("version=%s abi=%d last_error='%s'\n", version(), abi(), last());
    return (abi() == 1 && strstr(version(), "excise") != NULL) ? 0 : 1;
}
