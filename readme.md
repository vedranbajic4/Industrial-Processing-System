# Industrial Processing System

A thread-safe, priority-based industrial job processing API built in C#.

## Overview

Simulates processing of industrial jobs using a producer-consumer pattern with async task execution, worker threads, and an event-driven architecture.

## Features

- Thread-safe job submission and processing
- Priority queue (higher priority jobs processed first)
- Async job execution using `Task`
- Two job types: **Prime** (parallel prime counting) and **IO** (simulated I/O delay)
- Event system: `JobCompleted` / `JobFailed` with file logging
- Retry logic: up to 2 retries, then ABORT
- Periodic reports every minute (last 10 kept), generated with LINQ and saved as XML
- XML-based system configuration

## Configuration

System is initialized from an XML config file defining the number of worker threads and max queue size.

## Running

Build and run the main program. Worker threads will be started automatically based on the config file.
