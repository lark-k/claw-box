import { test } from 'node:test';
import assert from 'node:assert/strict';
import { add } from '../src/calculator.js';
test('adds positive numbers', () => assert.equal(add(2, 3), 5));
test('adds negative numbers', () => assert.equal(add(-2, 3), 1));
