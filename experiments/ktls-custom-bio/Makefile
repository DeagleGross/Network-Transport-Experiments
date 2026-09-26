CC ?= cc
CFLAGS ?= -O2 -g
CFLAGS += -std=c11 -Wall -Wextra -Wpedantic -Werror
LDLIBS += -lssl -lcrypto

.PHONY: all sanitize
all: bin/demo
bin/demo: demo.c
	mkdir -p bin
	$(CC) $(CFLAGS) $< $(LDLIBS) -o $@
sanitize: bin/demo-sanitize
bin/demo-sanitize: demo.c
	mkdir -p bin
	$(CC) $(CFLAGS) -O1 -fsanitize=address,undefined -fno-omit-frame-pointer $< $(LDLIBS) -o $@
