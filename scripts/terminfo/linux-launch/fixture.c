/* Source-built, disposable ELF test fixture. This is not the Ntilde application. */
#define _XOPEN_SOURCE 600
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>
#ifndef MODE
#define MODE 0
#endif
static volatile sig_atomic_t stopped;
static void stop(int signal_number) { (void)signal_number; stopped = 1; }
/* Wait only for fixture-owned termination; no terminal or desktop input is read. */
static void idle(void) {
    const struct timespec delay = {0, 1000000};
    while (!stopped) nanosleep(&delay, NULL);
}
/* The optional worker exists only on the private PTY created by this fixture parent. */
static int worker(const char *marker, const char *run) {
    char expected[96];
    snprintf(expected, sizeof(expected), "--ntilde-terminfo-worker=%s", run);
    if (strcmp(marker, expected) != 0 || MODE != 5) return 65;
    signal(SIGTERM, stop); signal(SIGINT, stop);
    if (write(1, "PRIVATE_PTY_FIXTURE\n", 20) != 20) return 70;
    idle(); return 0;
}
/* Accept only the retained test markers; this ELF is not the Ntilde application. */
int main(int argc, char **argv) {
    const char *run = getenv("TERMINFO_RUN_ID");
    if (!run || strlen(run) != 32) return 64;
    if (argc == 2) return worker(argv[1], run);
    char owner[96], clipboard[96];
    snprintf(owner, sizeof(owner), "--ntilde-terminfo-owner=%s", run);
    snprintf(clipboard, sizeof(clipboard), "--ntilde-terminfo-private-clipboard=%s", run);
    if (argc != 3 || strcmp(argv[1], owner) || strcmp(argv[2], clipboard)) return 64;
    if (MODE == 2) return 0;
    if (MODE == 3) return 7;
    signal(SIGTERM, MODE == 1 ? SIG_IGN : stop); signal(SIGINT, stop);
    pid_t child = -1; int master = -1;
    if (MODE == 5) {
        master = posix_openpt(O_RDWR | O_NOCTTY);
        if (master < 0 || grantpt(master) || unlockpt(master)) return 66;
        char *slave_name = ptsname(master);
        if (!slave_name) return 66;
        char slave[128]; snprintf(slave, sizeof(slave), "%s", slave_name);
        child = fork();
        if (child == 0) {
            close(master);
            if (setsid() < 0) _exit(67);
            int fd = open(slave, O_RDWR);
            if (fd < 0 || dup2(fd, 0) < 0 || dup2(fd, 1) < 0 || dup2(fd, 2) < 0) _exit(67);
            if (fd > 2) close(fd);
            char marker[96]; snprintf(marker, sizeof(marker), "--ntilde-terminfo-worker=%s", run);
            execl("/proc/self/exe", argv[0], marker, (char *)NULL);
            _exit(68);
        }
        if (child < 0) return 66;
        printf("{\"fixtureWorkerPid\":%d,\"pty\":\"%s\"}\n", (int)child, slave);
    }
    puts("READY_PRIVATE_ELF"); fflush(stdout);
    if (MODE == 4) {
        char bytes[4096]; memset(bytes, 'X', sizeof(bytes));
        for (int i = 0; i < 512 && !stopped; i++) {
            if (write(1, bytes, sizeof(bytes)) < 0 && errno != EINTR) break;
        }
    }
    idle();
    if (child > 0) {
        /* Only our own unreaped fixture child: this is not production launcher signaling. */
        kill(child, SIGTERM);
        int status; pid_t result;
        do { result = waitpid(child, &status, 0); } while (result < 0 && errno == EINTR);
        if (result != child) return 69;
        printf("FIXTURE_WORKER_REAPED %d %d\n", (int)child, status); fflush(stdout);
    }
    if (master >= 0) close(master);
    return 0;
}
